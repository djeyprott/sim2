using Sim.Core.Content;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Goods;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Settlements;
using Sim.Core.Technologies;

namespace Sim.Core.Polities;

/// <summary>The kinds of organization (design doc: organizations are a core concept). Saved as numbers, so only add to the end.</summary>
public enum OrganizationKind
{
    Polity = 0,
}

/// <summary>A polity's level. Saved as numbers, so only add to the end.</summary>
public enum PolityLevel
{
    Chiefdom = 0,
    State = 1,
}

/// <summary>A polity as other systems and the viewer see it.</summary>
public readonly record struct PolityInfo(
    int Id, string Name, PolityLevel Level, int Capital, long Founded, long Ended,
    double Aggression, double Greed, double Expansion, long StateSince)
{
    /// <summary>Whether the polity still stands.</summary>
    public bool Standing => Ended < 0;
}

/// <summary>How polities are founded and what they hold, read from polities.json.</summary>
public sealed class PolityParameters
{
    public double TemperamentSpread { get; set; } = 0.35;

    public double MinTemperament { get; set; } = 0.4;

    public double MaxTemperament { get; set; } = 2.5;

    public List<string> Positions { get; set; } = new List<string>();

    public double FoundPeople { get; set; } = 1500.0;

    public double FoundSurplusYears { get; set; } = 0.5;

    public double FoundSettledYears { get; set; } = 3000.0;

    public double FoundChance { get; set; } = 0.002;

    public double CrowdPeople { get; set; } = 50000.0;

    public double JoinChance { get; set; } = 0.1;

    public double UnlinkedJoinFactor { get; set; } = 0.5;

    public double ChiefdomReach { get; set; } = 4.0;

    public double StateReach { get; set; } = 10.0;

    public int ClaimNeighbours { get; set; } = 4;

    public double StatePeople { get; set; } = 20000.0;

    public int StateSettlements { get; set; } = 5;

    public double TributeShare { get; set; } = 0.15;

    public double MaxTributeShare { get; set; } = 0.5;

    public double EliteShare { get; set; } = 0.01;

    public double PeaceSoldierShare { get; set; } = 0.005;

    public double BorderSoldierShare { get; set; } = 0.02;

    public double StaffChangeRate { get; set; } = 0.2;

    public double MaxStaffShareOfFarmers { get; set; } = 0.5;

    public double UnrestRate { get; set; } = 0.1;

    public double HungerUnrest { get; set; } = 1.0;

    public double NeedsUnrest { get; set; } = 0.2;

    public double TributeUnrest { get; set; } = 1.5;

    public static PolityParameters LoadDefault() => Parse(ContentFiles.ReadText("polities.json"));

    public static PolityParameters Parse(string json)
    {
        PolityParameters p = ContentFiles.Parse<PolityParameters>(json, "polities");
        if (p.TemperamentSpread < 0.0 || p.MinTemperament <= 0.0 || p.MaxTemperament < p.MinTemperament
            || p.Positions.Any(string.IsNullOrWhiteSpace) || p.Positions.Distinct().Count() != p.Positions.Count)
        {
            throw new InvalidDataException("polities.json: temperament spread not negative, 0 < min <= max, and distinct named positions.");
        }

        double[] chances = { p.FoundChance, p.JoinChance, p.UnlinkedJoinFactor };
        if (chances.Any(c => c < 0.0 || c > 1.0) || p.FoundPeople <= 0.0 || p.FoundSurplusYears < 0.0 || p.FoundSettledYears < 0.0
            || p.CrowdPeople <= 0.0 || p.ChiefdomReach <= 0.0 || p.StateReach < p.ChiefdomReach || p.ClaimNeighbours < 1 || p.ClaimNeighbours > 6
            || p.StatePeople <= 0.0 || p.StateSettlements < 1
            || new[] { p.TributeShare, p.MaxTributeShare, p.EliteShare, p.PeaceSoldierShare, p.BorderSoldierShare, p.StaffChangeRate, p.MaxStaffShareOfFarmers, p.UnrestRate }.Any(v => v < 0.0 || v > 1.0)
            || p.HungerUnrest < 0.0 || p.NeedsUnrest < 0.0 || p.TributeUnrest < 0.0)
        {
            throw new InvalidDataException("polities.json: chances 0 to 1, people and reach above 0, state reach at least chiefdom reach, claimNeighbours 1 to 6.");
        }

        return p;
    }
}

/// <summary>
/// Polities (milestone 4). Holds the organizations table (every organization, of which polities
/// are the only kind for now), its positions table (empty until named people in milestone 6) and
/// which polity rules each land cell. A polity keeps its row after it ends, as history.
/// Each year (step 2): a polity whose capital settlement is gone ends; each polity lets go of
/// cells beyond its reach (trade-link cost from its capital: chiefdoms reach less than states),
/// draws in unruled settlements within reach (more readily those it trades with, and as its
/// temperament's expansion says), and claims unruled cells hemmed in by its own; large, crowded,
/// long-farmed settlements with grain to spare found new chiefdoms; ruled settlements give their
/// capital tribute, which feeds the elite and soldiers drawn from its farmers; every group's unrest
/// follows hunger, unmet needs and tribute; and chiefdoms that rule enough people and settlements
/// become states (and back). Runs once a year, after seafaring.
/// </summary>
public sealed class PolitySystem : ISimSystem
{
    public const string SystemName = "polities";

    /// <summary>Every organization: ids are never reused, and rows stay after an organization ends.</summary>
    public const string OrganizationTableName = "organizations";

    /// <summary>Each organization's positions: organization id, position name, holder (-1: nobody).</summary>
    public const string PositionTableName = "organizations.positions";

    /// <summary>The polity ruling each cell, plus one (0: none).</summary>
    public const string RulerField = "polities.ruler";

    /// <summary>The share of each good in its store that a ruled settlement gave its capital last year.</summary>
    public const string TakenField = "polities.taken";

    private readonly PolityParameters _parameters;
    private readonly TradeSystem? _trade;
    private readonly TradeParameters _tradeParameters = TradeParameters.LoadDefault();
    private IntColumn? _stateSince;
    private DoubleColumn? _tribute;
    private double[] _taken = Array.Empty<double>();
    private OccupationSet _occupations = OccupationSet.LoadDefault();
    private int _farmer = -1;
    private int _elite = -1;
    private int _soldier = -1;
    private PopulationGroups? _groups;
    private Stockpiles? _stockpiles;
    private int _grain = -1;
    private int[] _farmingSincePlusOne = Array.Empty<int>();
    private TechnologyUse? _technology;
    private TechnologyEffect[] _carrying = Array.Empty<TechnologyEffect>();
    private int[] _land = Array.Empty<int>();
    private double[] _stepCost = Array.Empty<double>();

    // Reach worked out for one polity at a time: cost from its capital, valid where stamped.
    private double[] _reachCost = Array.Empty<double>();
    private int[] _reachStamp = Array.Empty<int>();
    private int _stamp;
    private readonly List<int> _reached = new List<int>();
    private readonly PriorityQueue<int, double> _queue = new PriorityQueue<int, double>();
    private int[] _rulerPlusOne = Array.Empty<int>();
    private EntityTable? _organizations;
    private IntColumn? _kind;
    private StringColumn? _name;
    private IntColumn? _founded;
    private IntColumn? _ended;
    private IntColumn? _capital;
    private IntColumn? _level;
    private DoubleColumn? _aggression;
    private DoubleColumn? _greed;
    private DoubleColumn? _expansion;
    private EntityTable? _positions;
    private IntColumn? _positionOrganization;
    private StringColumn? _positionName;
    private IntColumn? _positionHolder;
    private WorldMap? _map;
    private StringColumn? _settlementName;
    private EntityTable? _settlements;
    private int[] _settlementPlusOne = Array.Empty<int>();

    /// <param name="trade">The trade system, whose links make settlements join more readily (none: no links).</param>
    public PolitySystem(PolityParameters parameters, TradeSystem? trade = null)
    {
        _parameters = parameters;
        _trade = trade;
    }

    public static PolitySystem CreateDefault(TradeSystem? trade = null) => new PolitySystem(PolityParameters.LoadDefault(), trade);

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public PolityParameters Parameters => _parameters;

    public void Register(WorldState state)
    {
        _rulerPlusOne = state.AddIntField(RulerField);
        _organizations = state.AddTable(OrganizationTableName);
        _kind = _organizations.AddIntColumn("kind");
        _name = _organizations.AddStringColumn("name");
        _founded = _organizations.AddIntColumn("founded");
        _ended = _organizations.AddIntColumn("ended");
        _capital = _organizations.AddIntColumn("capital");
        _level = _organizations.AddIntColumn("level");
        _aggression = _organizations.AddDoubleColumn("aggression");
        _greed = _organizations.AddDoubleColumn("greed");
        _expansion = _organizations.AddDoubleColumn("expansion");
        _stateSince = _organizations.AddIntColumn("stateSince");
        _tribute = _organizations.AddDoubleColumn("tribute");
        _taken = state.AddDoubleField(TakenField);
        _positions = state.AddTable(PositionTableName);
        _positionOrganization = _positions.AddIntColumn("organization");
        _positionName = _positions.AddStringColumn("position");
        _positionHolder = _positions.AddIntColumn("holder");
    }

    public void Initialize(SimContext context)
    {
        WorldState state = context.State;
        _map = context.Map;
        if (state.HasTable(SettlementSystem.TableName))
        {
            _settlements = state.Table(SettlementSystem.TableName);
            _settlementName = _settlements.StringColumn("name");
            _settlementPlusOne = state.IntField(SettlementSystem.SettlementField);
        }

        _groups = new PopulationGroups(state);
        _farmer = _occupations.IndexOf(OccupationSet.Farmer);
        _elite = _occupations.Contains(OccupationSet.Elite) ? _occupations.IndexOf(OccupationSet.Elite) : -1;
        _soldier = _occupations.Contains(OccupationSet.Soldier) ? _occupations.IndexOf(OccupationSet.Soldier) : -1;
        if (state.HasTable(Stockpiles.LedgerTableName))
        {
            _stockpiles = new Stockpiles(state, GoodSet.LoadDefault());
            _grain = _stockpiles.Goods.IndexOf(GoodSet.Grain);
        }

        _farmingSincePlusOne = state.HasField(FarmingSystem.SinceField) ? state.IntField(FarmingSystem.SinceField) : Array.Empty<int>();
        _technology = TechnologyUse.TryCreate(state, context.Map);
        _carrying = _technology?.WithEffect(TechnologySet.LandCarrying) ?? Array.Empty<TechnologyEffect>();
        WorldMap map = context.Map;
        _land = Enumerable.Range(0, map.CellCount).Where(map.IsLand).ToArray();
        _stepCost = new double[map.CellCount];
        for (int cell = 0; cell < map.CellCount; cell++)
        {
            _stepCost[cell] = !map.IsLand(cell) ? double.PositiveInfinity : map.Terrain(cell) switch
            {
                TerrainClass.Hills => _tradeParameters.TerrainCost["hills"],
                TerrainClass.Mountains => _tradeParameters.TerrainCost["mountains"],
                _ => _tradeParameters.TerrainCost["lowland"],
            };
        }

        _reachCost = new double[map.CellCount];
        _reachStamp = new int[map.CellCount];
    }

    public void Update(SimContext context)
    {
        if (_settlementPlusOne.Length == 0)
        {
            return;
        }

        long tick = context.Tick;
        Rng rng = context.Rng;

        // Polities whose capital settlement is gone end (collapse into successors comes in step 5).
        foreach (PolityInfo p in Standing())
        {
            if (_settlementPlusOne[p.Capital] <= 0)
            {
                End(p.Id, tick);
            }
        }

        // Which settlements each settlement trades with, by cell.
        var linked = new HashSet<(int, int)>();
        if (_trade != null)
        {
            foreach ((int a, int b) in _trade.LinksByCell())
            {
                linked.Add((a, b));
                linked.Add((b, a));
            }
        }

        // Each polity, in id order: let go of what is out of reach, draw in settlements, fill gaps.
        List<int>[] cellsOf = CellsByPolity();
        foreach (PolityInfo p in Standing())
        {
            double reach = p.Level == PolityLevel.State ? _parameters.StateReach : _parameters.ChiefdomReach;
            FillReach(p.Capital, reach);
            foreach (int cell in cellsOf[p.Id])
            {
                if (!Reached(cell))
                {
                    _rulerPlusOne[cell] = 0;
                }
            }

            var ownSettlements = new List<int>();
            foreach (int cell in cellsOf[p.Id])
            {
                if (RulerOf(cell) == p.Id && _settlementPlusOne[cell] > 0)
                {
                    ownSettlements.Add(cell);
                }
            }

            foreach (int cell in _reached)
            {
                if (_settlementPlusOne[cell] <= 0 || RulerOf(cell) >= 0)
                {
                    continue;
                }

                bool trades = ownSettlements.Any(own => linked.Contains((own, cell)));
                double chance = _parameters.JoinChance * p.Expansion * (1.0 - _reachCost[cell] / reach)
                    * (trades ? 1.0 : _parameters.UnlinkedJoinFactor);
                if (rng.Chance(Math.Min(1.0, chance)))
                {
                    Rule(cell, p.Id);
                    ClaimAround(cell, p.Id);
                }
            }

            // Unruled land within reach hemmed in by the polity's own cells is claimed.
            foreach (int cell in _reached)
            {
                if (RulerOf(cell) >= 0)
                {
                    continue;
                }

                int own = 0;
                foreach (int other in _map!.Grid.Neighbours(cell))
                {
                    own += RulerOf(other) == p.Id ? 1 : 0;
                }

                if (own >= _parameters.ClaimNeighbours)
                {
                    Rule(cell, p.Id);
                }
            }
        }

        // New chiefdoms, in settlement id order.
        EntityTable settlements = _settlements!;
        IntColumn settlementCell = settlements.IntColumn("cell");
        DoubleColumn settlementPeople = settlements.DoubleColumn("population");
        for (int id = 0; id < settlements.IdCount; id++)
        {
            if (!settlements.IsAlive(id))
            {
                continue;
            }

            int cell = settlementCell[id];
            if (RulerOf(cell) >= 0 || settlementPeople[id] < _parameters.FoundPeople || !FarmedFor(cell, _parameters.FoundSettledYears, tick))
            {
                continue;
            }

            double people = _groups!.CellTotal(cell);
            if (_stockpiles != null && _grain >= 0 && _stockpiles.Stock(cell, _grain) < _parameters.FoundSurplusYears * people)
            {
                continue;
            }

            double crowd = people;
            foreach (int other in _map!.Grid.Neighbours(cell))
            {
                crowd += _groups.CellTotal(other);
            }

            if (rng.Chance(_parameters.FoundChance * Math.Min(1.0, crowd / _parameters.CrowdPeople)))
            {
                Found(cell, tick, rng);
            }
        }

        // Tribute, the elite and soldiers, and unrest.
        cellsOf = CellsByPolity();
        TakeTribute(cellsOf);
        Staff(cellsOf);
        UpdateUnrest();

        // Chiefdoms that rule enough people and settlements are states, and back.
        foreach (PolityInfo p in Standing())
        {
            double people = 0.0;
            int towns = 0;
            foreach (int cell in cellsOf[p.Id])
            {
                people += _groups!.CellTotal(cell);
                towns += _settlementPlusOne[cell] > 0 ? 1 : 0;
            }

            bool state = people >= _parameters.StatePeople && towns >= _parameters.StateSettlements;
            if (state != (p.Level == PolityLevel.State))
            {
                SetLevel(p.Id, state ? PolityLevel.State : PolityLevel.Chiefdom, tick);
            }
        }
    }

    /// <summary>The grain a polity's capital took in tribute last year.</summary>
    public double Tribute(int polity) => _tribute![polity];

    /// <summary>The share of each good a ruled settlement gave its capital last year (0 for others).</summary>
    public double Taken(int cell) => _taken[cell];

    // Each ruled settlement but the capital gives the capital a share of each good in its store:
    // tributeShare times the polity's greed, less the farther it is (1 - cost / reach) and the
    // more restless its people are (1 - unrest), at most maxTributeShare.
    private void TakeTribute(List<int>[] cellsOf)
    {
        foreach (int cell in _land)
        {
            _taken[cell] = 0.0;
        }

        if (_stockpiles == null)
        {
            return;
        }

        foreach (PolityInfo p in Standing())
        {
            double reach = p.Level == PolityLevel.State ? _parameters.StateReach : _parameters.ChiefdomReach;
            FillReach(p.Capital, reach);
            double grain = 0.0;
            foreach (int cell in cellsOf[p.Id])
            {
                if (cell == p.Capital || _settlementPlusOne[cell] <= 0 || !Reached(cell))
                {
                    continue;
                }

                double near = 1.0 - _reachCost[cell] / reach;
                double share = Math.Min(_parameters.MaxTributeShare, _parameters.TributeShare * p.Greed * near * (1.0 - CellUnrest(cell)));
                if (share <= 0.0)
                {
                    continue;
                }

                _taken[cell] = share;
                for (int good = 0; good < _stockpiles.Goods.Count; good++)
                {
                    double moved = _stockpiles.Transfer(cell, p.Capital, good, share * _stockpiles.Stock(cell, good));
                    grain += good == _grain ? moved : 0.0;
                }
            }

            _tribute![p.Id] = grain;
        }
    }

    // In each capital the elite and soldiers move toward what the polity wants, drawn from the
    // capital's farmers and sent back to them: elite eliteShare of the people it rules; soldiers
    // peaceSoldierShare of them, or borderSoldierShare where it borders another polity, times its
    // aggression; both together no more than last year's grain tribute feeds. Elite and soldiers
    // anywhere but a standing polity's capital go back to farming.
    private void Staff(List<int>[] cellsOf)
    {
        if (_elite < 0 || _soldier < 0)
        {
            return;
        }

        PopulationGroups groups = _groups!;
        var capitals = new HashSet<int>();
        foreach (PolityInfo p in Standing())
        {
            capitals.Add(p.Capital);
            double people = 0.0;
            bool border = false;
            foreach (int cell in cellsOf[p.Id])
            {
                people += groups.CellTotal(cell);
                foreach (int other in _map!.Grid.Neighbours(cell))
                {
                    int ruler = RulerOf(other);
                    border |= ruler >= 0 && ruler != p.Id;
                }
            }

            double elite = _parameters.EliteShare * people;
            double soldiers = (border ? _parameters.BorderSoldierShare : _parameters.PeaceSoldierShare) * p.Aggression * people;
            double fed = _tribute![p.Id];
            if (elite + soldiers > fed)
            {
                double scale = fed / (elite + soldiers);
                elite *= scale;
                soldiers *= scale;
            }

            MoveToward(p.Capital, _elite, elite);
            MoveToward(p.Capital, _soldier, soldiers);
        }

        for (int group = 0; group < groups.IdCount; group++)
        {
            if (!groups.IsAlive(group) || groups.Size(group) <= 0.0)
            {
                continue;
            }

            int occupation = groups.Occupation(group);
            int cell = groups.Cell(group);
            if ((occupation == _elite || occupation == _soldier) && !capitals.Contains(cell) && _settlementPlusOne[cell] > 0)
            {
                double people = groups.Size(group);
                double food = groups.Food(group), everyday = groups.Everyday(group), luxury = groups.Luxury(group);
                int farmers = groups.FindOrAdd(cell, _farmer);
                groups.AddPeople(farmers, people, food, everyday, luxury, group);
                groups.SetSize(group, 0.0);
            }
        }
    }

    // Moves a capital's group of an occupation a step toward a target size, from or to its farmers.
    private void MoveToward(int cell, int occupation, double target)
    {
        PopulationGroups groups = _groups!;
        int farmerGroup = groups.Find(cell, _farmer);
        int group = groups.Find(cell, occupation);
        double now = group >= 0 ? groups.Size(group) : 0.0;
        double change = _parameters.StaffChangeRate * (target - now);
        if (change > 0.0 && farmerGroup >= 0)
        {
            change = Math.Min(change, _parameters.MaxStaffShareOfFarmers * groups.Size(farmerGroup));
            if (change <= 0.0)
            {
                return;
            }

            group = groups.FindOrAdd(cell, occupation);
            farmerGroup = groups.Find(cell, _farmer);
            if (farmerGroup < 0)
            {
                return;
            }

            groups.AddPeople(group, change, groups.Food(farmerGroup), groups.Everyday(farmerGroup), groups.Luxury(farmerGroup), farmerGroup);
            groups.SetSize(farmerGroup, groups.Size(farmerGroup) - change);
        }
        else if (change < 0.0 && group >= 0)
        {
            double leaving = Math.Min(-change, now);
            int farmers = groups.FindOrAdd(cell, _farmer);
            group = groups.Find(cell, occupation);
            groups.AddPeople(farmers, leaving, groups.Food(group), groups.Everyday(group), groups.Luxury(group), group);
            groups.SetSize(group, now - leaving);
        }
    }

    // Each group's unrest moves unrestRate of the way toward hungerUnrest x (1 - food met) +
    // needsUnrest x (1 - everyday needs met) + tributeUnrest x the share its settlement gave in
    // tribute, at most 1.
    private void UpdateUnrest()
    {
        PopulationGroups groups = _groups!;
        for (int group = 0; group < groups.IdCount; group++)
        {
            if (!groups.IsAlive(group) || groups.Size(group) <= 0.0)
            {
                continue;
            }

            double target = Math.Min(1.0, _parameters.HungerUnrest * (1.0 - groups.Food(group))
                + _parameters.NeedsUnrest * (1.0 - groups.Everyday(group))
                + _parameters.TributeUnrest * _taken[groups.Cell(group)]);
            double unrest = groups.Unrest(group);
            groups.SetUnrest(group, unrest + _parameters.UnrestRate * (target - unrest));
        }
    }

    /// <summary>The size-weighted unrest of a cell's people (0 where nobody lives).</summary>
    public double CellUnrest(int cell)
    {
        PopulationGroups groups = _groups!;
        double people = 0.0, unrest = 0.0;
        foreach (int group in groups.InCell(cell))
        {
            people += groups.Size(group);
            unrest += groups.Size(group) * groups.Unrest(group);
        }

        return people > 0.0 ? unrest / people : 0.0;
    }

    // Whether farming has stood in a cell at least this many years.
    private bool FarmedFor(int cell, double years, long tick) =>
        _farmingSincePlusOne.Length > 0 && _farmingSincePlusOne[cell] > 0
        && tick - (_farmingSincePlusOne[cell] - 1) >= years * GameDate.MonthsPerYear;

    // The land cells each polity rules, by polity id (ended polities' lists are empty).
    private List<int>[] CellsByPolity()
    {
        var lists = new List<int>[_organizations!.IdCount];
        for (int i = 0; i < lists.Length; i++)
        {
            lists[i] = new List<int>();
        }

        foreach (int cell in _land)
        {
            int ruler = _rulerPlusOne[cell] - 1;
            if (ruler >= 0)
            {
                lists[ruler].Add(cell);
            }
        }

        return lists;
    }

    // Rules the unruled land cells next to a cell, where they are within the reach worked out last.
    private void ClaimAround(int cell, int polity)
    {
        foreach (int other in _map!.Grid.Neighbours(cell))
        {
            if (_map.IsLand(other) && RulerOf(other) < 0 && Reached(other))
            {
                Rule(other, polity);
            }
        }
    }

    private bool Reached(int cell) => _reachStamp[cell] == _stamp;

    /// <summary>
    /// The cost of reaching a cell from a polity's capital, as trade carries goods over land
    /// (trade.json terrain costs, cheaper along rivers, and cheaper still where the capital uses
    /// the wheel or riding), or infinity beyond its reach.
    /// </summary>
    public double ReachCost(int polity, int cell)
    {
        PolityInfo p = Info(polity);
        FillReach(p.Capital, p.Level == PolityLevel.State ? _parameters.StateReach : _parameters.ChiefdomReach);
        return Reached(cell) ? _reachCost[cell] : double.PositiveInfinity;
    }

    // Fills the reach of a capital: every land cell within maxCost, with its cost, in _reached.
    private void FillReach(int capital, double maxCost)
    {
        WorldMap map = _map!;
        _stamp++;
        _reached.Clear();
        double carrying = _technology?.CellFactor(capital, _carrying) ?? 1.0;
        _queue.Clear();
        _reachCost[capital] = 0.0;
        _reachStamp[capital] = _stamp;
        _reached.Add(capital);
        _queue.Enqueue(capital, 0.0);
        while (_queue.TryDequeue(out int cell, out double cost))
        {
            if (cost > _reachCost[cell])
            {
                continue;
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
                    step *= _tradeParameters.RiverFactor;
                }

                double total = cost + step * carrying;
                if (total > maxCost || (_reachStamp[next] == _stamp && total >= _reachCost[next]))
                {
                    continue;
                }

                if (_reachStamp[next] != _stamp)
                {
                    _reached.Add(next);
                }

                _reachStamp[next] = _stamp;
                _reachCost[next] = total;
                _queue.Enqueue(next, total);
            }
        }
    }

    /// <summary>The polity ruling a cell, or -1.</summary>
    public int RulerOf(int cell) => _rulerPlusOne[cell] - 1;

    /// <summary>Sets the polity ruling a cell (-1: none). Only land cells can be ruled.</summary>
    public void Rule(int cell, int polity)
    {
        if (polity >= 0 && (!_map!.IsLand(cell) || !IsStanding(polity)))
        {
            throw new ArgumentException($"Cell {cell} cannot be ruled by polity {polity}.");
        }

        _rulerPlusOne[cell] = polity + 1;
    }

    /// <summary>Whether an organization id names a polity that still stands.</summary>
    public bool IsStanding(int polity) =>
        polity >= 0 && polity < _organizations!.IdCount && _organizations.IsAlive(polity)
        && _kind![polity] == (int)OrganizationKind.Polity && _ended![polity] < 0;

    /// <summary>
    /// Founds a chiefdom with its capital in a settled cell, named after the capital, with a
    /// temperament drawn from the stream given and its positions (held by nobody). It rules the
    /// capital and the unruled land cells next to it. Returns its id.
    /// </summary>
    public int Found(int capital, long tick, Rng rng)
    {
        if (!_map!.IsLand(capital) || RulerOf(capital) >= 0)
        {
            throw new ArgumentException($"A polity cannot be founded in cell {capital}.");
        }

        EntityTable table = _organizations!;
        int id = table.Add();
        _kind![id] = (int)OrganizationKind.Polity;
        int settlement = _settlementPlusOne.Length > 0 ? _settlementPlusOne[capital] - 1 : -1;
        _name![id] = settlement >= 0 ? _settlementName![settlement] ?? "" : "";
        _founded![id] = (int)tick;
        _ended![id] = -1;
        _capital![id] = capital;
        _level![id] = (int)PolityLevel.Chiefdom;
        _stateSince![id] = -1;
        _tribute![id] = 0.0;
        _aggression![id] = Temperament(rng);
        _greed![id] = Temperament(rng);
        _expansion![id] = Temperament(rng);
        foreach (string position in _parameters.Positions)
        {
            int row = _positions!.Add();
            _positionOrganization![row] = id;
            _positionName![row] = position;
            _positionHolder![row] = -1;
        }

        Rule(capital, id);
        foreach (int other in _map.Grid.Neighbours(capital))
        {
            if (_map.IsLand(other) && RulerOf(other) < 0)
            {
                Rule(other, id);
            }
        }

        return id;
    }

    /// <summary>Sets a standing polity's level (it counts as a state from this tick if it never was one).</summary>
    public void SetLevel(int polity, PolityLevel level, long tick)
    {
        if (!IsStanding(polity))
        {
            throw new ArgumentException($"Polity {polity} does not stand.");
        }

        _level![polity] = (int)level;
        if (level == PolityLevel.State && _stateSince![polity] < 0)
        {
            _stateSince[polity] = (int)tick;
        }
    }

    /// <summary>Ends a polity: it rules no cells any more, and its row stays as history.</summary>
    public void End(int polity, long tick)
    {
        if (!IsStanding(polity))
        {
            throw new ArgumentException($"Polity {polity} does not stand.");
        }

        _ended![polity] = (int)tick;
        foreach (int cell in _land)
        {
            if (_rulerPlusOne[cell] == polity + 1)
            {
                _rulerPlusOne[cell] = 0;
            }
        }
    }

    /// <summary>One polity, standing or ended.</summary>
    public PolityInfo Info(int polity) => new PolityInfo(
        polity, _name![polity] ?? "", (PolityLevel)_level![polity], _capital![polity], _founded![polity], _ended![polity],
        _aggression![polity], _greed![polity], _expansion![polity], _stateSince![polity]);

    /// <summary>Every polity ever founded (standing and ended), in id order.</summary>
    public List<PolityInfo> All()
    {
        var list = new List<PolityInfo>();
        EntityTable table = _organizations!;
        for (int id = 0; id < table.IdCount; id++)
        {
            if (table.IsAlive(id) && _kind![id] == (int)OrganizationKind.Polity)
            {
                list.Add(Info(id));
            }
        }

        return list;
    }

    /// <summary>The polities standing now, in id order.</summary>
    public List<PolityInfo> Standing() => All().Where(p => p.Standing).ToList();

    /// <summary>A polity's positions and their holders (-1: nobody), in the order of polities.json.</summary>
    public List<(string Position, int Holder)> Positions(int polity)
    {
        var list = new List<(string, int)>();
        EntityTable table = _positions!;
        for (int row = 0; row < table.IdCount; row++)
        {
            if (table.IsAlive(row) && _positionOrganization![row] == polity)
            {
                list.Add((_positionName![row] ?? "", _positionHolder![row]));
            }
        }

        return list;
    }

    private double Temperament(Rng rng) =>
        Math.Clamp(Math.Exp(_parameters.TemperamentSpread * rng.NextGaussian()), _parameters.MinTemperament, _parameters.MaxTemperament);
}
