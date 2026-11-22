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
    double Aggression, double Greed, double Expansion, long StateSince, int Parent)
{
    /// <summary>Whether the polity still stands.</summary>
    public bool Standing => Ended < 0;

    /// <summary>Whether it broke away from another polity (Parent is that polity).</summary>
    public bool Successor => Parent >= 0;
}

/// <summary>What happened to a polity (the events table).</summary>
public enum PolityEventKind
{
    /// <summary>Founded at a settlement no polity ruled.</summary>
    Founded = 0,

    /// <summary>Broke away from another polity (Other) as its successor.</summary>
    BrokeAway = 1,

    /// <summary>Became a state.</summary>
    BecameState = 2,

    /// <summary>Fell back from state to chiefdom.</summary>
    BecameChiefdom = 3,

    /// <summary>Went to war on another polity (Other).</summary>
    WarStarted = 4,

    /// <summary>Won a war against another polity (Other), which made peace.</summary>
    Peace = 5,

    /// <summary>Conquered by another polity (Other), which took its capital.</summary>
    Conquered = 6,

    /// <summary>Split: Amount cells broke away.</summary>
    Split = 7,

    /// <summary>Ended when its capital settlement was abandoned.</summary>
    Fell = 8,
}

/// <summary>One event in a polity's history: where (a cell) and when, with another polity if one took part.</summary>
public readonly record struct PolityEvent(PolityEventKind Kind, int Polity, int Other, int Cell, long Tick, double Amount);

/// <summary>A war between two polities, under way (Ended below 0) or over.</summary>
public readonly record struct WarInfo(
    int Id, int Attacker, int Defender, long Started, long Ended, int Winner, bool Conquest,
    double AttackerDeaths, double DefenderDeaths, int AttackerTook, int DefenderTook)
{
    public bool UnderWay => Ended < 0;
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

    public double StateSettledYears { get; set; } = 5000.0;

    public double FoundChance { get; set; } = 0.002;

    public double CrowdPeople { get; set; } = 50000.0;

    public double JoinChance { get; set; } = 0.1;

    public double UnlinkedJoinFactor { get; set; } = 0.5;

    public double ChiefdomReach { get; set; } = 4.0;

    public double StateReach { get; set; } = 10.0;

    public int ClaimNeighbours { get; set; } = 4;

    public double StatePeople { get; set; } = 20000.0;

    public int StateSettlements { get; set; } = 5;

    public double StateKeepShare { get; set; } = 0.7;

    public double TributeShare { get; set; } = 0.15;

    public double MaxTributeShare { get; set; } = 0.5;

    public double EliteShare { get; set; } = 0.01;

    public double PeaceSoldierShare { get; set; } = 0.005;

    public double BorderSoldierShare { get; set; } = 0.02;

    public double StaffChangeRate { get; set; } = 0.2;

    public double MaxStaffShare { get; set; } = 0.3;

    public double UnrestRate { get; set; } = 0.1;

    public double HungerUnrest { get; set; } = 1.0;

    public double NeedsUnrest { get; set; } = 0.2;

    public double TributeUnrest { get; set; } = 1.5;

    public double WarSoldierShare { get; set; } = 0.04;

    public double WarThreshold { get; set; } = 0.6;

    public double WarChance { get; set; } = 0.2;

    public double MaxStrengthRatio { get; set; } = 4.0;

    public double HungerWar { get; set; } = 1.0;

    public double CovetWar { get; set; } = 0.5;

    public double PeaceYears { get; set; } = 20.0;

    public double WeaponStrength { get; set; } = 0.5;

    public double MaxWeaponsPerSoldier { get; set; } = 2.0;

    public double MinDistanceFactor { get; set; } = 0.25;

    public double MilitiaShare { get; set; } = 0.01;

    public double TakeRate { get; set; } = 0.3;

    public double SoldierLoss { get; set; } = 0.2;

    public double CivilianLoss { get; set; } = 0.02;

    public double BurnShare { get; set; } = 0.5;

    public double PlunderShare { get; set; } = 0.25;

    public double RazeChance { get; set; } = 0.03;

    public double RazeDeaths { get; set; } = 0.6;

    public double ExhaustionLimit { get; set; } = 0.3;

    public double MaxWarYears { get; set; } = 15.0;

    public double CohesionUnrest { get; set; } = 1.5;

    public double CohesionFamine { get; set; } = 1.0;

    public double CohesionStretch { get; set; } = 0.65;

    public double CohesionWar { get; set; } = 1.0;

    public double CohesionDefeat { get; set; } = 0.2;

    public double DefeatYears { get; set; } = 20.0;

    public double SplitThreshold { get; set; } = 0.35;

    public double SplitChance { get; set; } = 0.05;

    public double SplitDistance { get; set; } = 0.5;

    public double RevoltUnrest { get; set; } = 0.4;

    public double SuccessorPeople { get; set; } = 1500.0;

    public int MaxSuccessors { get; set; } = 2;

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
        if (chances.Any(c => c < 0.0 || c > 1.0) || p.FoundPeople <= 0.0 || p.FoundSurplusYears < 0.0 || p.FoundSettledYears < 0.0 || p.StateSettledYears < 0.0
            || p.CrowdPeople <= 0.0 || p.ChiefdomReach <= 0.0 || p.StateReach < p.ChiefdomReach || p.ClaimNeighbours < 1 || p.ClaimNeighbours > 6
            || p.StatePeople <= 0.0 || p.StateSettlements < 1 || p.StateKeepShare <= 0.0 || p.StateKeepShare > 1.0
            || new[] { p.TributeShare, p.MaxTributeShare, p.EliteShare, p.PeaceSoldierShare, p.BorderSoldierShare, p.StaffChangeRate, p.MaxStaffShare, p.UnrestRate }.Any(v => v < 0.0 || v > 1.0)
            || p.HungerUnrest < 0.0 || p.NeedsUnrest < 0.0 || p.TributeUnrest < 0.0)
        {
            throw new InvalidDataException("polities.json: chances 0 to 1, people and reach above 0, state reach at least chiefdom reach, claimNeighbours 1 to 6.");
        }

        if (new[] { p.WarSoldierShare, p.WarChance, p.MinDistanceFactor, p.MilitiaShare, p.TakeRate, p.SoldierLoss, p.CivilianLoss, p.BurnShare, p.PlunderShare, p.RazeChance, p.RazeDeaths }.Any(v => v < 0.0 || v > 1.0)
            || p.BurnShare + p.PlunderShare > 1.0 || p.MaxStrengthRatio < 1.0 || p.HungerWar < 0.0 || p.CovetWar < 0.0
            || p.PeaceYears < 0.0 || p.WeaponStrength < 0.0 || p.MaxWeaponsPerSoldier < 0.0 || p.ExhaustionLimit <= 0.0 || p.MaxWarYears < 1.0)
        {
            throw new InvalidDataException("polities.json: war shares and chances 0 to 1 (burn and plunder together at most 1), maxStrengthRatio at least 1, exhaustionLimit above 0, maxWarYears at least 1.");
        }

        if (p.CohesionUnrest < 0.0 || p.CohesionFamine < 0.0 || p.CohesionStretch < 0.0 || p.CohesionWar < 0.0 || p.CohesionDefeat < 0.0
            || p.DefeatYears < 0.0 || p.SplitThreshold <= 0.0 || p.SplitThreshold > 1.0 || p.SplitChance < 0.0 || p.SplitChance > 1.0
            || p.SplitDistance < 0.0 || p.SplitDistance > 1.0 || p.RevoltUnrest < 0.0 || p.SuccessorPeople <= 0.0 || p.MaxSuccessors < 0)
        {
            throw new InvalidDataException("polities.json: cohesion weights not negative, splitThreshold above 0 and at most 1, splitChance and splitDistance 0 to 1, successorPeople above 0.");
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
/// become states (and back). Wars (step 4): polities at war fight a year over their border cells,
/// and polities at peace may declare war on a neighbour; the wars table keeps every war as history.
/// Collapse (step 5): a polity short of cohesion (unrest, hunger, stretched reach, war and
/// defeat) may split, its far and restless provinces breaking away as successor chiefdoms; one
/// that loses its capital breaks into successors too. Runs every month, after seafaring: growing
/// and keeping whole each month, everything else each January.
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

    /// <summary>Every war: attacker, defender, when it started and ended, winner, losses and cells taken.</summary>
    public const string WarTableName = "polities.wars";

    /// <summary>Every founding, breakaway, change of level, war, peace, conquest, split and fall.</summary>
    public const string EventTableName = "polities.events";

    /// <summary>1 on cells of a polity at war or on its borders with another polity (from the last update), else 0.</summary>
    public const string ThreatenedField = "polities.threatened";

    private readonly PolityParameters _parameters;
    private int[] _threatened = Array.Empty<int>();
    private int[] _warOf = Array.Empty<int>();
    private int _warOfWars = -1;
    private bool _warOfDirty = true;
    private EntityTable? _wars;
    private EntityTable? _events;
    private IntColumn? _eventKind;
    private IntColumn? _eventPolity;
    private IntColumn? _eventOther;
    private IntColumn? _eventCell;
    private IntColumn? _eventTick;
    private DoubleColumn? _eventAmount;
    private IntColumn? _warAttacker;
    private IntColumn? _warDefender;
    private IntColumn? _warStarted;
    private IntColumn? _warEnded;
    private IntColumn? _warWinner;
    private IntColumn? _warConquest;
    private DoubleColumn? _warAttackerPeople;
    private DoubleColumn? _warDefenderPeople;
    private IntColumn? _warAttackerCells;
    private IntColumn? _warDefenderCells;
    private DoubleColumn? _warAttackerDeaths;
    private DoubleColumn? _warDefenderDeaths;
    private IntColumn? _warAttackerTook;
    private IntColumn? _warDefenderTook;
    private TechnologyEffect[] _defence = Array.Empty<TechnologyEffect>();
    private TechnologyEffect[] _attack = Array.Empty<TechnologyEffect>();
    private int _copperWeapons = -1;
    private int _bronzeWeapons = -1;
    private readonly TradeSystem? _trade;
    private readonly TradeParameters _tradeParameters = TradeParameters.LoadDefault();
    private IntColumn? _stateSince;
    private DoubleColumn? _tribute;
    private IntColumn? _parent;
    private DoubleColumn? _cohesion;
    private IntColumn? _splits;
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
    private int[] _reachFrom = Array.Empty<int>();
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

    // Monthly: polities grow, and land cut off from its capital breaks away, every month; the
    // rest (founding, war, splits, tribute, the elite and soldiers, unrest, levels) runs each January.
    public int IntervalMonths => 1;

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
        _parent = _organizations.AddIntColumn("parent");
        _cohesion = _organizations.AddDoubleColumn("cohesion");
        _splits = _organizations.AddIntColumn("splits");
        _taken = state.AddDoubleField(TakenField);
        _positions = state.AddTable(PositionTableName);
        _positionOrganization = _positions.AddIntColumn("organization");
        _positionName = _positions.AddStringColumn("position");
        _positionHolder = _positions.AddIntColumn("holder");
        _threatened = state.AddIntField(ThreatenedField);
        _wars = state.AddTable(WarTableName);
        _warAttacker = _wars.AddIntColumn("attacker");
        _warDefender = _wars.AddIntColumn("defender");
        _warStarted = _wars.AddIntColumn("started");
        _warEnded = _wars.AddIntColumn("ended");
        _warWinner = _wars.AddIntColumn("winner");
        _warConquest = _wars.AddIntColumn("conquest");
        _warAttackerPeople = _wars.AddDoubleColumn("attackerPeople");
        _warDefenderPeople = _wars.AddDoubleColumn("defenderPeople");
        _warAttackerCells = _wars.AddIntColumn("attackerCells");
        _warDefenderCells = _wars.AddIntColumn("defenderCells");
        _warAttackerDeaths = _wars.AddDoubleColumn("attackerDeaths");
        _warDefenderDeaths = _wars.AddDoubleColumn("defenderDeaths");
        _warAttackerTook = _wars.AddIntColumn("attackerTook");
        _warDefenderTook = _wars.AddIntColumn("defenderTook");
        _events = state.AddTable(EventTableName);
        _eventKind = _events.AddIntColumn("kind");
        _eventPolity = _events.AddIntColumn("polity");
        _eventOther = _events.AddIntColumn("other");
        _eventCell = _events.AddIntColumn("cell");
        _eventTick = _events.AddIntColumn("tick");
        _eventAmount = _events.AddDoubleColumn("amount");
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
            _copperWeapons = _stockpiles.Goods.Contains(GoodSet.CopperWeapons) ? _stockpiles.Goods.IndexOf(GoodSet.CopperWeapons) : -1;
            _bronzeWeapons = _stockpiles.Goods.Contains(GoodSet.BronzeWeapons) ? _stockpiles.Goods.IndexOf(GoodSet.BronzeWeapons) : -1;
        }

        _farmingSincePlusOne = state.HasField(FarmingSystem.SinceField) ? state.IntField(FarmingSystem.SinceField) : Array.Empty<int>();
        _technology = TechnologyUse.TryCreate(state, context.Map);
        _carrying = _technology?.WithEffect(TechnologySet.LandCarrying) ?? Array.Empty<TechnologyEffect>();
        _defence = _technology?.WithEffect(TechnologySet.Defence) ?? Array.Empty<TechnologyEffect>();
        _attack = _technology?.WithEffect(TechnologySet.Attack) ?? Array.Empty<TechnologyEffect>();
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
        _reachFrom = new int[map.CellCount];
    }

    public void Update(SimContext context)
    {
        if (_settlementPlusOne.Length == 0)
        {
            return;
        }

        long tick = context.Tick;
        Rng rng = context.Rng;
        _warOfDirty = true;
        bool january = tick % GameDate.MonthsPerYear == 0;

        // Polities whose capital settlement is gone end, their other settlements becoming successors.
        if (january)
        {
            foreach (PolityInfo p in Standing())
            {
                if (IsStanding(p.Id) && _settlementPlusOne[p.Capital] <= 0)
                {
                    Fall(p.Id, tick, rng);
                }
            }
        }

        // Between Januaries there is nothing to do until a polity stands.
        if (!january && !AnyStanding())
        {
            return;
        }

        long clock = System.Diagnostics.Stopwatch.GetTimestamp();
        bool released = false;

        // Each polity, in id order: let go of what is out of reach (going round other polities'
        // land), draw in settlements with the land on the way to them, fill gaps.
        List<int>[] cellsOf = CellsByPolity();
        foreach (PolityInfo p in Standing())
        {
            double reach = p.Level == PolityLevel.State ? _parameters.StateReach : _parameters.ChiefdomReach;
            FillReach(p.Capital, reach, p.Id);
            foreach (int cell in cellsOf[p.Id])
            {
                if (!Reached(cell))
                {
                    _rulerPlusOne[cell] = 0;
                    released = true;
                }
            }


            foreach (int cell in _reached)
            {
                if (_settlementPlusOne[cell] <= 0 || RulerOf(cell) >= 0)
                {
                    continue;
                }

                // The yearly chance, spread over the twelve months.
                // Whether it trades with one of the polity's settlements.
                int polity = p.Id;
                bool trades = _trade != null && _trade.TradesWith(_settlementPlusOne[cell] - 1,
                    other => RulerOf(other) == polity && _settlementPlusOne[other] > 0);
                double yearly = Math.Min(1.0, _parameters.JoinChance * p.Expansion * (1.0 - _reachCost[cell] / reach)
                    * (trades ? 1.0 : _parameters.UnlinkedJoinFactor));
                if (rng.Chance(1.0 - Math.Pow(1.0 - yearly, 1.0 / GameDate.MonthsPerYear)))
                {
                    // The unruled land on the way from the capital comes too, so the polity stays whole.
                    for (int on = cell; on >= 0 && RulerOf(on) != p.Id; on = _reachFrom[on])
                    {
                        Rule(on, p.Id);
                    }

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

        GrowSeconds += Since(clock);
        if (january)
        {
            Yearly(tick, rng);
        }
        else if (released)
        {
            // Letting go of land is the only monthly change that can cut a polity in two.
            clock = System.Diagnostics.Stopwatch.GetTimestamp();
            KeepWhole(tick, rng);
            WholeSeconds += Since(clock);
        }
    }

    // Whether any polity stands.
    private bool AnyStanding()
    {
        EntityTable table = _organizations!;
        for (int id = 0; id < table.IdCount; id++)
        {
            if (table.IsAlive(id) && _ended![id] < 0)
            {
                return true;
            }
        }

        return false;
    }

    // Each January: new chiefdoms, wars, splits, land cut off breaking away, tribute, the elite and
    // soldiers, unrest, and levels.
    private void Yearly(long tick, Rng rng)
    {
        long clock = System.Diagnostics.Stopwatch.GetTimestamp();

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

        FoundSeconds += Since(clock);

        // Wars: those under way are fought a year, then new ones may be declared.
        clock = System.Diagnostics.Stopwatch.GetTimestamp();
        Wars(tick, rng);
        WarSeconds += Since(clock);

        // Polities short of cohesion may split.
        clock = System.Diagnostics.Stopwatch.GetTimestamp();
        Collapse(tick, rng);
        CollapseSeconds += Since(clock);

        // Land cut off from its capital breaks away.
        clock = System.Diagnostics.Stopwatch.GetTimestamp();
        KeepWhole(tick, rng);
        WholeSeconds += Since(clock);
        clock = System.Diagnostics.Stopwatch.GetTimestamp();

        // Tribute, the elite and soldiers, and unrest.
        List<int>[] cellsOf = CellsByPolity();
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

            // A state stays one until it falls below stateKeepShare of the thresholds; a chiefdom
            // becomes one only once its capital has been farmed stateSettledYears.
            double keep = p.Level == PolityLevel.State ? _parameters.StateKeepShare : 1.0;
            bool state = people >= keep * _parameters.StatePeople && towns >= keep * _parameters.StateSettlements
                && (p.Level == PolityLevel.State || FarmedFor(p.Capital, _parameters.StateSettledYears, tick));
            if (state != (p.Level == PolityLevel.State))
            {
                SetLevel(p.Id, state ? PolityLevel.State : PolityLevel.Chiefdom, tick);
            }
        }

        RuleSeconds += Since(clock);
    }

    // ---- Collapse (step 5) ----

    // Each standing polity's cohesion, in id order: 1 - cohesionUnrest x its people's unrest -
    // cohesionFamine x their unmet food - cohesionStretch x how far its people live from the
    // capital (cost over reach) - cohesionWar x its exhaustion in a war under way - cohesionDefeat
    // if it lost a war in the last defeatYears. Below splitThreshold it splits with chance
    // splitChance x (splitThreshold - cohesion) / splitThreshold: its far cells (cost at least
    // splitDistance of its reach) and the cells of its restless settlements (unrest at least
    // revoltUnrest) break away, as successors where a settlement is large enough.
    private void Collapse(long tick, Rng rng)
    {
        List<int>[] cellsOf = CellsByPolity();
        var lostWar = new Dictionary<int, long>();
        EntityTable wars = _wars!;
        for (int war = 0; war < wars.IdCount; war++)
        {
            if (!wars.IsAlive(war) || _warEnded![war] < 0 || _warWinner![war] < 0)
            {
                continue;
            }

            int loser = _warWinner[war] == _warAttacker![war] ? _warDefender![war] : _warAttacker[war];
            lostWar[loser] = Math.Max(_warEnded[war], lostWar.TryGetValue(loser, out long t) ? t : long.MinValue);
        }

        foreach (PolityInfo p in Standing())
        {
            if (!IsStanding(p.Id))
            {
                continue;
            }

            double reach = p.Level == PolityLevel.State ? _parameters.StateReach : _parameters.ChiefdomReach;
            FillReach(p.Capital, reach);
            double people = 0.0, unrest = 0.0, hunger = 0.0, stretch = 0.0;
            var cost = new Dictionary<int, double>();
            foreach (int cell in cellsOf[p.Id])
            {
                double far = Reached(cell) ? _reachCost[cell] / reach : 1.0;
                cost[cell] = far;
                foreach (int group in _groups!.InCell(cell))
                {
                    double size = _groups.Size(group);
                    people += size;
                    unrest += size * _groups.Unrest(group);
                    hunger += size * (1.0 - _groups.Food(group));
                    stretch += size * far;
                }
            }

            double cohesion = 1.0;
            if (people > 0.0)
            {
                cohesion -= (_parameters.CohesionUnrest * unrest + _parameters.CohesionFamine * hunger + _parameters.CohesionStretch * stretch) / people;
            }

            int war = WarOf(p.Id);
            if (war >= 0)
            {
                cohesion -= _parameters.CohesionWar * Exhaustion(war, p.Id);
            }

            if (lostWar.TryGetValue(p.Id, out long ended) && tick - ended < _parameters.DefeatYears * GameDate.MonthsPerYear)
            {
                cohesion -= _parameters.CohesionDefeat;
            }

            _cohesion![p.Id] = cohesion;
            if (cohesion >= _parameters.SplitThreshold
                || !rng.Chance(_parameters.SplitChance * (_parameters.SplitThreshold - cohesion) / _parameters.SplitThreshold))
            {
                continue;
            }

            // The far and restless land breaks away; the capital never does.
            var leaving = new List<int>();
            foreach (int cell in cellsOf[p.Id])
            {
                if (cell != p.Capital && (cost[cell] >= _parameters.SplitDistance
                    || (_settlementPlusOne[cell] > 0 && CellUnrest(cell) >= _parameters.RevoltUnrest)))
                {
                    leaving.Add(cell);
                }
            }

            // A restless settlement takes the land next to it that it rules.
            var leavingSet = new HashSet<int>(leaving);
            foreach (int cell in leaving.ToList())
            {
                if (_settlementPlusOne[cell] <= 0)
                {
                    continue;
                }

                foreach (int other in _map!.Grid.Neighbours(cell))
                {
                    if (other != p.Capital && RulerOf(other) == p.Id && leavingSet.Add(other))
                    {
                        leaving.Add(other);
                    }
                }
            }

            if (leaving.Count == 0)
            {
                continue;
            }

            leaving.Sort();
            _splits![p.Id]++;
            Record(PolityEventKind.Split, p.Id, -1, p.Capital, tick, leaving.Count);
            BreakAway(p.Id, leaving, tick, rng);
        }
    }

    // Cells of a polity break away from it: in turn, the largest settlement among them with at
    // least successorPeople people founds a successor chiefdom, which takes the breaking cells
    // within its reach, up to maxSuccessors; what is left falls back to no rule.
    private void BreakAway(int polity, List<int> cells, long tick, Rng rng)
    {
        DoubleColumn population = _settlements!.DoubleColumn("population");
        var towns = cells.Where(c => _settlementPlusOne[c] > 0 && RulerOf(c) == polity && population[_settlementPlusOne[c] - 1] >= _parameters.SuccessorPeople)
            .OrderByDescending(c => population[_settlementPlusOne[c] - 1]).ThenBy(c => c).ToList();
        int made = 0;
        foreach (int town in towns)
        {
            if (made >= _parameters.MaxSuccessors)
            {
                break;
            }

            if (RulerOf(town) != polity)
            {
                continue;
            }

            made++;

            // The successor takes the breaking land within its reach that joins its capital.
            int successor = NewPolity(town, tick, rng, polity);
            FillReach(town, _parameters.ChiefdomReach);
            var breaking = new HashSet<int>(cells);
            var stack = new Stack<int>();
            stack.Push(town);
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                foreach (int next in _map!.Grid.Neighbours(at))
                {
                    if (breaking.Contains(next) && RulerOf(next) == polity && Reached(next))
                    {
                        Rule(next, successor);
                        stack.Push(next);
                    }
                }
            }
        }

        foreach (int cell in cells)
        {
            if (RulerOf(cell) == polity)
            {
                _rulerPlusOne[cell] = 0;
            }
        }
    }

    // Each standing polity's land must join its capital, cell to neighbouring cell. Land cut off
    // (by war, a split or a lost settlement) breaks away, each cut-off piece on its own: its
    // large settlements become successors, the rest falls back to no rule.
    private void KeepWhole(long tick, Rng rng)
    {
        List<int>[] cellsOf = CellsByPolity();
        foreach (PolityInfo p in Standing())
        {
            List<int> cells = cellsOf[p.Id];
            if (cells.Count <= 1)
            {
                continue;
            }

            var joined = new HashSet<int> { p.Capital };
            var stack = new Stack<int>();
            stack.Push(p.Capital);
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                foreach (int next in _map!.Grid.Neighbours(at))
                {
                    if (RulerOf(next) == p.Id && joined.Add(next))
                    {
                        stack.Push(next);
                    }
                }
            }

            if (joined.Count == cells.Count)
            {
                continue;
            }

            // The cut-off pieces, each found from its lowest cell.
            var seen = new HashSet<int>(joined);
            foreach (int start in cells)
            {
                if (!seen.Add(start))
                {
                    continue;
                }

                var piece = new List<int> { start };
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int at = stack.Pop();
                    foreach (int next in _map!.Grid.Neighbours(at))
                    {
                        if (RulerOf(next) == p.Id && seen.Add(next))
                        {
                            piece.Add(next);
                            stack.Push(next);
                        }
                    }
                }

                piece.Sort();
                BreakAway(p.Id, piece, tick, rng);
            }
        }
    }

    /// <summary>Whether all of a standing polity's land joins its capital, cell to neighbouring cell.</summary>
    public bool IsWhole(int polity)
    {
        int capital = Info(polity).Capital;
        int count = 0;
        foreach (int cell in _land)
        {
            count += RulerOf(cell) == polity ? 1 : 0;
        }

        var joined = new HashSet<int> { capital };
        var stack = new Stack<int>();
        stack.Push(capital);
        while (stack.Count > 0)
        {
            int at = stack.Pop();
            foreach (int next in _map!.Grid.Neighbours(at))
            {
                if (RulerOf(next) == polity && joined.Add(next))
                {
                    stack.Push(next);
                }
            }
        }

        return joined.Count == count;
    }

    // A polity that has lost its capital settlement ends, its other land breaking into successors.
    private void Fall(int polity, long tick, Rng rng, bool conquered = false)
    {
        if (!conquered)
        {
            Record(PolityEventKind.Fell, polity, -1, Info(polity).Capital, tick, 0.0);
        }

        List<int> cells = CellsByPolity()[polity];
        BreakAway(polity, cells, tick, rng);
        End(polity, tick);
    }

    private void Record(PolityEventKind kind, int polity, int other, int cell, long tick, double amount)
    {
        int row = _events!.Add();
        _eventKind![row] = (int)kind;
        _eventPolity![row] = polity;
        _eventOther![row] = other;
        _eventCell![row] = cell;
        _eventTick![row] = (int)tick;
        _eventAmount![row] = amount;
    }

    /// <summary>Every polity event so far, in the order they happened.</summary>
    public List<PolityEvent> Events()
    {
        var list = new List<PolityEvent>();
        for (int row = 0; row < _events!.IdCount; row++)
        {
            if (_events.IsAlive(row))
            {
                list.Add(new PolityEvent((PolityEventKind)_eventKind![row], _eventPolity![row], _eventOther![row], _eventCell![row], _eventTick![row], _eventAmount![row]));
            }
        }

        return list;
    }

    /// <summary>Time spent in each part, for the headless runner's timings (not saved).</summary>
    public double GrowSeconds { get; private set; }

    public double WholeSeconds { get; private set; }

    public double FoundSeconds { get; private set; }

    public double WarSeconds { get; private set; }

    public double CollapseSeconds { get; private set; }

    public double RuleSeconds { get; private set; }

    private static double Since(long start) => System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;

    /// <summary>A polity's cohesion last year (1: whole; low values risk a split).</summary>
    public double Cohesion(int polity) => _cohesion![polity];

    /// <summary>How many times a polity has split.</summary>
    public int Splits(int polity) => _splits![polity];

    // ---- War (step 4) ----

    // One year of war, then declarations. Wars under way are fought in id order: a side that no
    // longer stands loses; each contested border cell may change hands, with the local strengths
    // deciding the chance; soldiers and the people of contested cells die; taken settlements are
    // burned and plundered and may be razed; taking the other's capital conquers it; exhaustion or
    // the years ends a war in peace. Then each polity at peace weighs its neighbours and may
    // declare war on the one it has the strongest case against. Last, the threatened cells.
    private void Wars(long tick, Rng rng)
    {
        _warOfDirty = true;
        EntityTable wars = _wars!;
        Dictionary<long, List<int>> fronts = Borders(out _);
        for (int war = 0; war < wars.IdCount; war++)
        {
            if (!wars.IsAlive(war) || _warEnded![war] >= 0)
            {
                continue;
            }

            int a = _warAttacker![war], d = _warDefender![war];
            if (!IsStanding(a) || !IsStanding(d))
            {
                EndWar(war, tick, IsStanding(a) ? a : IsStanding(d) ? d : -1, false);
                continue;
            }

            FightYear(war, tick, rng, fronts);
        }

        Dictionary<long, List<int>> borders = Borders(out SortedSet<int>[] neighbours);
        Declare(tick, rng, neighbours);

        // Threatened cells: every cell of a polity at war, and every cell on a border.
        foreach (int cell in _land)
        {
            int ruler = RulerOf(cell);
            _threatened[cell] = ruler >= 0 && AtWar(ruler) ? 1 : 0;
        }

        foreach (List<int> cells in borders.Values)
        {
            foreach (int cell in cells)
            {
                _threatened[cell] = 1;
            }
        }
    }

    // The cells of each polity next to each other polity's, keyed by Key(own, other), in cell
    // order; and each polity's neighbours in id order. Only looked up by key, never iterated in
    // dictionary order for anything that changes state.
    private Dictionary<long, List<int>> Borders(out SortedSet<int>[] neighbours)
    {
        var borders = new Dictionary<long, List<int>>();
        neighbours = new SortedSet<int>[_organizations!.IdCount];
        for (int i = 0; i < neighbours.Length; i++)
        {
            neighbours[i] = new SortedSet<int>();
        }

        foreach (int cell in _land)
        {
            int own = RulerOf(cell);
            if (own < 0)
            {
                continue;
            }

            foreach (int other in _map!.Grid.Neighbours(cell))
            {
                int ruler = RulerOf(other);
                if (ruler < 0 || ruler == own)
                {
                    continue;
                }

                neighbours[own].Add(ruler);
                long key = Key(own, ruler);
                if (!borders.TryGetValue(key, out List<int>? list))
                {
                    list = new List<int>();
                    borders[key] = list;
                }

                if (list.Count == 0 || list[^1] != cell)
                {
                    list.Add(cell);
                }
            }
        }

        return borders;
    }

    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

    // Each polity at peace, in id order, weighs each neighbour at peace it has not fought within
    // peaceYears: aggression x (strength ratio - 1, the ratio at most maxStrengthRatio) +
    // hungerWar x its capital farmers' unmet food + covetWar x (the neighbour's share of the two
    // capitals' grain - 1/2). The strongest case above warThreshold leads to war with chance warChance.
    private void Declare(long tick, Rng rng, SortedSet<int>[] neighbours)
    {
        var lastPeace = new Dictionary<long, long>();
        EntityTable wars = _wars!;
        for (int war = 0; war < wars.IdCount; war++)
        {
            if (wars.IsAlive(war) && _warEnded![war] >= 0)
            {
                int a = _warAttacker![war], d = _warDefender![war];
                long ended = _warEnded[war];
                foreach (long key in new[] { Key(a, d), Key(d, a) })
                {
                    lastPeace[key] = Math.Max(ended, lastPeace.TryGetValue(key, out long t) ? t : long.MinValue);
                }
            }
        }

        foreach (PolityInfo p in Standing())
        {
            double own = Strength(p.Id);
            if (AtWar(p.Id) || own <= 0.0)
            {
                continue;
            }

            int best = -1;
            double bestCase = _parameters.WarThreshold;
            foreach (int other in neighbours[p.Id])
            {
                if (!IsStanding(other) || AtWar(other)
                    || (lastPeace.TryGetValue(Key(p.Id, other), out long ended) && tick - ended < _parameters.PeaceYears * GameDate.MonthsPerYear))
                {
                    continue;
                }

                double theirs = Strength(other);
                double ratio = theirs > 0.0 ? Math.Min(_parameters.MaxStrengthRatio, own / theirs) : _parameters.MaxStrengthRatio;
                double hunger = 1.0 - CapitalFood(p.Id);
                double ownGrain = CapitalGrain(p.Id), theirGrain = CapitalGrain(other);
                double covet = ownGrain + theirGrain > 0.0 ? theirGrain / (ownGrain + theirGrain) - 0.5 : 0.0;
                double warCase = p.Aggression * (ratio - 1.0) + _parameters.HungerWar * hunger + _parameters.CovetWar * covet;
                if (warCase > bestCase)
                {
                    bestCase = warCase;
                    best = other;
                }
            }

            if (best >= 0 && rng.Chance(_parameters.WarChance))
            {
                StartWar(p.Id, best, tick);
            }
        }
    }

    /// <summary>Starts a war between two standing polities at peace. Returns its id.</summary>
    public int StartWar(int attacker, int defender, long tick)
    {
        if (!IsStanding(attacker) || !IsStanding(defender) || attacker == defender || AtWar(attacker) || AtWar(defender))
        {
            throw new ArgumentException($"Polities {attacker} and {defender} cannot go to war.");
        }

        List<int>[] cellsOf = CellsByPolity();
        int war = _wars!.Add();
        _warOfDirty = true;
        _warAttacker![war] = attacker;
        _warDefender![war] = defender;
        _warStarted![war] = (int)tick;
        _warEnded![war] = -1;
        _warWinner![war] = -1;
        _warConquest![war] = 0;
        _warAttackerPeople![war] = PeopleIn(cellsOf[attacker]);
        _warDefenderPeople![war] = PeopleIn(cellsOf[defender]);
        _warAttackerCells![war] = cellsOf[attacker].Count;
        _warDefenderCells![war] = cellsOf[defender].Count;
        _warAttackerDeaths![war] = 0.0;
        _warDefenderDeaths![war] = 0.0;
        _warAttackerTook![war] = 0;
        _warDefenderTook![war] = 0;
        Record(PolityEventKind.WarStarted, attacker, defender, Info(defender).Capital, tick, 0.0);
        return war;
    }

    // One year of a war between two standing polities, over the borders as they stood at the
    // start of the year (a cell is only fought over while its holder still holds it).
    private void FightYear(int war, long tick, Rng rng, Dictionary<long, List<int>> borders)
    {
        int a = _warAttacker![war], d = _warDefender![war];
        double strengthA = Strength(a), strengthD = Strength(d);
        List<int> towardD = borders.TryGetValue(Key(d, a), out List<int>? x) ? x : new List<int>();
        List<int> towardA = borders.TryGetValue(Key(a, d), out List<int>? y) ? y : new List<int>();

        // Distance factors for every contested cell, from each side's capital.
        var contested = towardD.Concat(towardA).ToList();
        Dictionary<int, double> nearA = DistanceFactors(a, contested);
        Dictionary<int, double> nearD = DistanceFactors(d, contested);

        // Soldiers fall in proportion to the other side's share of the strength, the attacker's
        // counting its chariots and the defender's the walls of its capital (at the square root of
        // their factor, as walls guard only part of a war): storming walls, or facing chariots,
        // costs lives as well as ground.
        double fightA = strengthA * AttackFactor(a), fightD = strengthD * Math.Sqrt(DefenceFactor(Info(d).Capital));
        double total = fightA + fightD;
        if (total > 0.0)
        {
            _warAttackerDeaths![war] += KillSoldiers(a, _parameters.SoldierLoss * fightD / total);
            _warDefenderDeaths![war] += KillSoldiers(d, _parameters.SoldierLoss * fightA / total);
        }

        // The defender's cells along the border, then the attacker's (the defender strikes back).
        int capitalD = Info(d).Capital, capitalA = Info(a).Capital;
        bool conquered = false;
        foreach ((List<int> cells, int taker, int holder, double takerStrength, double holderStrength, Dictionary<int, double> nearTaker, Dictionary<int, double> nearHolder) in new[]
        {
            (towardD, a, d, strengthA, strengthD, nearA, nearD),
            (towardA, d, a, strengthD, strengthA, nearD, nearA),
        })
        {
            foreach (int cell in cells)
            {
                if (RulerOf(cell) != holder || conquered)
                {
                    continue;
                }

                double people = _groups!.CellTotal(cell);
                // Only land within the taker's reach can be taken; the holder fights at least at minDistanceFactor.
                double attack = takerStrength * nearTaker[cell] * (_map!.Terrain(cell) == TerrainClass.Lowland ? CapitalFactor(taker, _attack) : 1.0);
                double defend = holderStrength * Math.Max(_parameters.MinDistanceFactor, nearHolder[cell]) + _parameters.MilitiaShare * people;
                defend *= WallsFactor(cell, holder);

                double deaths = KillShare(cell, _parameters.CivilianLoss);
                AddDeaths(war, holder, deaths);
                if (attack + defend <= 0.0 || !rng.Chance(_parameters.TakeRate * attack / (attack + defend)))
                {
                    continue;
                }

                Rule(cell, taker);
                if (taker == a)
                {
                    _warAttackerTook![war]++;
                }
                else
                {
                    _warDefenderTook![war]++;
                }

                if (_settlementPlusOne[cell] > 0)
                {
                    Sack(cell, Info(taker).Capital);
                    if (rng.Chance(_parameters.RazeChance))
                    {
                        AddDeaths(war, holder, KillShare(cell, _parameters.RazeDeaths));
                        _stockpiles?.LoseAll(cell);
                    }
                }

                if (cell == (holder == d ? capitalD : capitalA))
                {
                    Conquer(taker, holder, tick, rng);
                    EndWar(war, tick, taker, true);
                    conquered = true;
                }
            }
        }

        if (conquered)
        {
            return;
        }

        // Peace when either side is exhausted (deaths over its people at the start, plus the
        // share of its cells it has lost), or after maxWarYears; the less exhausted side wins.
        double exhaustA = Exhaustion(war, a), exhaustD = Exhaustion(war, d);
        bool tooLong = tick - _warStarted![war] >= (_parameters.MaxWarYears - 1.0) * GameDate.MonthsPerYear;
        if (exhaustA >= _parameters.ExhaustionLimit || exhaustD >= _parameters.ExhaustionLimit || tooLong)
        {
            EndWar(war, tick, exhaustA < exhaustD ? a : d, false);
        }
    }

    private double Exhaustion(int war, int side)
    {
        bool attacker = side == _warAttacker![war];
        double people = attacker ? _warAttackerPeople![war] : _warDefenderPeople![war];
        double deaths = attacker ? _warAttackerDeaths![war] : _warDefenderDeaths![war];
        int cells = attacker ? _warAttackerCells![war] : _warDefenderCells![war];
        int lost = attacker ? _warDefenderTook![war] : _warAttackerTook![war];
        return (people > 0.0 ? deaths / people : 0.0) + (cells > 0 ? (double)lost / cells : 0.0);
    }

    private void AddDeaths(int war, int side, double deaths)
    {
        if (side == _warAttacker![war])
        {
            _warAttackerDeaths![war] += deaths;
        }
        else
        {
            _warDefenderDeaths![war] += deaths;
        }
    }

    private void EndWar(int war, long tick, int winner, bool conquest)
    {
        _warOfDirty = true;
        _warEnded![war] = (int)tick;
        _warWinner![war] = winner;
        _warConquest![war] = conquest ? 1 : 0;
        int a = _warAttacker![war], d = _warDefender![war];
        if (!conquest && winner >= 0)
        {
            int loser = winner == a ? d : a;
            Record(PolityEventKind.Peace, winner, loser, IsStanding(loser) ? Info(loser).Capital : Info(winner).Capital, tick, 0.0);
        }
    }

    // The conqueror takes the beaten polity's capital and every one of its cells within reach;
    // the beaten polity ends and the rest of its land breaks into successors (or no rule).
    private void Conquer(int winner, int loser, long tick, Rng rng)
    {
        List<int> cells = CellsByPolity()[loser];
        PolityInfo w = Info(winner);
        FillReach(w.Capital, w.Level == PolityLevel.State ? _parameters.StateReach : _parameters.ChiefdomReach);
        foreach (int cell in cells)
        {
            if (Reached(cell))
            {
                Rule(cell, winner);
            }
        }

        Rule(Info(loser).Capital, winner);
        Record(PolityEventKind.Conquered, loser, winner, Info(loser).Capital, tick, cells.Count);
        Fall(loser, tick, rng, conquered: true);
    }

    // A taken settlement: burnShare of its store is lost, plunderShare goes to the taker's capital.
    private void Sack(int cell, int capital)
    {
        if (_stockpiles == null)
        {
            return;
        }

        if (_parameters.PlunderShare > 0.0)
        {
            for (int good = 0; good < _stockpiles.Goods.Count; good++)
            {
                _stockpiles.Transfer(cell, capital, good, _parameters.PlunderShare * _stockpiles.Stock(cell, good));
            }
        }

        double left = 1.0 - _parameters.PlunderShare;
        _stockpiles.LoseShare(cell, left > 0.0 ? _parameters.BurnShare / left : 1.0);
    }

    // For each cell, max(minDistanceFactor, 1 - cost from the polity's capital / its reach), or 0 beyond its reach.
    private Dictionary<int, double> DistanceFactors(int polity, List<int> cells)
    {
        PolityInfo p = Info(polity);
        double reach = p.Level == PolityLevel.State ? _parameters.StateReach : _parameters.ChiefdomReach;
        FillReach(p.Capital, reach);
        var factors = new Dictionary<int, double>();
        foreach (int cell in cells)
        {
            factors[cell] = Reached(cell) ? Math.Max(_parameters.MinDistanceFactor, 1.0 - _reachCost[cell] / reach) : 0.0;
        }

        return factors;
    }

    /// <summary>
    /// A polity's strength: the soldiers in its capital, times 1 + weaponStrength x the weapons in
    /// its capital's store per soldier (bronze ones count twice, at most maxWeaponsPerSoldier),
    /// plus a levy of militiaShare of its capital's people (so a new polity is not defenceless).
    /// </summary>
    public double Strength(int polity)
    {
        double levy = _parameters.MilitiaShare * _groups!.CellTotal(Info(polity).Capital);
        double soldiers = Soldiers(polity);
        if (soldiers <= 0.0)
        {
            return levy;
        }

        return levy + soldiers * (1.0 + _parameters.WeaponStrength * Math.Min(_parameters.MaxWeaponsPerSoldier, Weapons(polity) / soldiers));
    }

    /// <summary>The soldiers in a polity's capital.</summary>
    public double Soldiers(int polity)
    {
        if (_soldier < 0)
        {
            return 0.0;
        }

        int group = _groups!.Find(Info(polity).Capital, _soldier);
        return group >= 0 ? _groups.Size(group) : 0.0;
    }

    /// <summary>The weapons in a polity's capital's store, bronze ones counting twice.</summary>
    public double Weapons(int polity)
    {
        if (_stockpiles == null)
        {
            return 0.0;
        }

        int capital = Info(polity).Capital;
        return (_copperWeapons >= 0 ? _stockpiles.Stock(capital, _copperWeapons) : 0.0)
            + (_bronzeWeapons >= 0 ? 2.0 * _stockpiles.Stock(capital, _bronzeWeapons) : 0.0);
    }

    // Walls: the strongest fortification factor of a settlement the holder rules in the cell or
    // next to it (a walled town shelters the country around it), or 1.
    private double WallsFactor(int cell, int holder)
    {
        if (_technology == null || _defence.Length == 0)
        {
            return 1.0;
        }

        double best = _settlementPlusOne[cell] > 0 ? _technology.CellFactor(cell, _defence) : 1.0;
        foreach (int other in _map!.Grid.Neighbours(cell))
        {
            if (_settlementPlusOne[other] > 0 && RulerOf(other) == holder)
            {
                best = Math.Max(best, _technology.CellFactor(other, _defence));
            }
        }

        return best;
    }

    /// <summary>How much stronger a polity attacks on open lowland for the chariots its capital uses (1: none).</summary>
    public double AttackFactor(int polity) => CapitalFactor(polity, _attack);

    /// <summary>How much stronger a settlement's defenders are for the fortifications its people use (1: none).</summary>
    public double DefenceFactor(int cell) => _technology?.CellFactor(cell, _defence) ?? 1.0;

    // The product of a set of technology effects the capital's people use (1 when none).
    private double CapitalFactor(int polity, TechnologyEffect[] effects) =>
        _technology?.CellFactor(Info(polity).Capital, effects) ?? 1.0;

    private double CapitalFood(int polity)
    {
        int group = _groups!.Find(Info(polity).Capital, _farmer);
        return group >= 0 ? _groups.Food(group) : 1.0;
    }

    private double CapitalGrain(int polity) =>
        _stockpiles != null && _grain >= 0 ? _stockpiles.Stock(Info(polity).Capital, _grain) : 0.0;

    private double PeopleIn(List<int> cells)
    {
        double people = 0.0;
        foreach (int cell in cells)
        {
            people += _groups!.CellTotal(cell);
        }

        return people;
    }

    // Kills a share of a polity's soldiers. Returns the deaths.
    private double KillSoldiers(int polity, double share)
    {
        int group = _soldier >= 0 ? _groups!.Find(Info(polity).Capital, _soldier) : -1;
        if (group < 0)
        {
            return 0.0;
        }

        double size = _groups!.Size(group);
        double deaths = size * Math.Clamp(share, 0.0, 1.0);
        _groups.SetSize(group, size - deaths);
        return deaths;
    }

    // Kills a share of every group in a cell. Returns the deaths.
    private double KillShare(int cell, double share)
    {
        share = Math.Clamp(share, 0.0, 1.0);
        double deaths = 0.0;
        foreach (int group in _groups!.InCell(cell))
        {
            double size = _groups.Size(group);
            deaths += size * share;
            _groups.SetSize(group, size * (1.0 - share));
        }

        return deaths;
    }

    /// <summary>Whether a polity is in a war under way.</summary>
    public bool AtWar(int polity) => WarOf(polity) >= 0;

    /// <summary>The war under way a polity is in, or -1.</summary>
    public int WarOf(int polity)
    {
        // Worked out from the wars table when it or the organizations table has changed; not
        // saved, as it follows from the saved tables.
        EntityTable wars = _wars!;
        if (_warOf.Length != _organizations!.IdCount || _warOfWars != wars.IdCount || _warOfDirty)
        {
            _warOf = new int[_organizations.IdCount];
            Array.Fill(_warOf, -1);
            for (int war = 0; war < wars.IdCount; war++)
            {
                if (wars.IsAlive(war) && _warEnded![war] < 0)
                {
                    _warOf[_warAttacker![war]] = war;
                    _warOf[_warDefender![war]] = war;
                }
            }

            _warOfWars = wars.IdCount;
            _warOfDirty = false;
        }

        return polity >= 0 && polity < _warOf.Length ? _warOf[polity] : -1;
    }

    /// <summary>One war, under way or over.</summary>
    public WarInfo War(int war) => new WarInfo(
        war, _warAttacker![war], _warDefender![war], _warStarted![war], _warEnded![war], _warWinner![war], _warConquest![war] != 0,
        _warAttackerDeaths![war], _warDefenderDeaths![war], _warAttackerTook![war], _warDefenderTook![war]);

    /// <summary>Every war, in id order.</summary>
    public List<WarInfo> Wars()
    {
        var list = new List<WarInfo>();
        for (int war = 0; war < _wars!.IdCount; war++)
        {
            if (_wars.IsAlive(war))
            {
                list.Add(War(war));
            }
        }

        return list;
    }

    /// <summary>Whether a cell is threatened: its polity is at war or it lies on a border (last update).</summary>
    public bool Threatened(int cell) => _threatened[cell] != 0;

    /// <summary>The grain a polity's capital took in tribute last year.</summary>
    public double Tribute(int polity) => _tribute![polity];

    /// <summary>The share of each good a ruled settlement gave its capital last year (0 for others).</summary>
    public double Taken(int cell) => _taken[cell];

    // Each ruled settlement but the capital gives the capital a share of each good people use
    // (food, everyday goods and luxuries; not raw materials, which stay to be traded and worked)
    // in its store: tributeShare times the polity's greed, less the farther it is (1 - cost /
    // reach) and the more restless its people are (1 - unrest), at most maxTributeShare.
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
                    if (_stockpiles.Goods.Goods[good].NeedTier == NeedTier.None)
                    {
                        continue;
                    }

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
            double share = AtWar(p.Id) ? _parameters.WarSoldierShare : border ? _parameters.BorderSoldierShare : _parameters.PeaceSoldierShare;
            double soldiers = share * p.Aggression * people;
            double fed = _tribute![p.Id];
            if (elite + soldiers > fed)
            {
                double scale = fed / (elite + soldiers);
                elite *= scale;
                soldiers *= scale;
            }

            // Together they are at most maxStaffShare of the capital's people, so its farmers stay.
            double cap = _parameters.MaxStaffShare * groups.CellTotal(p.Capital);
            if (elite + soldiers > cap)
            {
                double scale = cap / (elite + soldiers);
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
            change = Math.Min(change, groups.Size(farmerGroup));
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

    // Fills the reach of a capital: every land cell within maxCost, with its cost and the cell it
    // was reached from, in _reached. With a polity given, the reach runs only over that polity's
    // land and unruled land, never across another polity's.
    private void FillReach(int capital, double maxCost, int over = -1)
    {
        WorldMap map = _map!;
        _stamp++;
        _reached.Clear();
        double carrying = _technology?.CellFactor(capital, _carrying) ?? 1.0;
        _queue.Clear();
        _reachCost[capital] = 0.0;
        _reachStamp[capital] = _stamp;
        _reachFrom[capital] = -1;
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
                if (double.IsInfinity(step) || (over >= 0 && _rulerPlusOne[next] != 0 && _rulerPlusOne[next] != over + 1))
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
                _reachFrom[next] = cell;
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

        // Land taken by a polity at war is threatened at once, not from the next January.
        if (polity >= 0 && _threatened.Length > 0 && AtWar(polity))
        {
            _threatened[cell] = 1;
        }
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

        int id = NewPolity(capital, tick, rng, -1);
        foreach (int other in _map.Grid.Neighbours(capital))
        {
            if (_map.IsLand(other) && RulerOf(other) < 0)
            {
                Rule(other, id);
            }
        }

        return id;
    }

    // A new chiefdom ruling its capital cell (taken from whoever ruled it), with a fresh
    // temperament and empty positions; parent is the polity it broke away from, or -1.
    private int NewPolity(int capital, long tick, Rng rng, int parent)
    {
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
        _parent![id] = parent;
        _cohesion![id] = 1.0;
        _splits![id] = 0;
        foreach (string position in _parameters.Positions)
        {
            int row = _positions!.Add();
            _positionOrganization![row] = id;
            _positionName![row] = position;
            _positionHolder![row] = -1;
        }

        Rule(capital, id);
        Record(parent >= 0 ? PolityEventKind.BrokeAway : PolityEventKind.Founded, id, parent, capital, tick, 0.0);
        return id;
    }

    /// <summary>Sets a standing polity's level (it counts as a state from this tick if it never was one).</summary>
    public void SetLevel(int polity, PolityLevel level, long tick)
    {
        if (!IsStanding(polity))
        {
            throw new ArgumentException($"Polity {polity} does not stand.");
        }

        if (_level![polity] != (int)level)
        {
            Record(level == PolityLevel.State ? PolityEventKind.BecameState : PolityEventKind.BecameChiefdom, polity, -1, _capital![polity], tick, 0.0);
        }

        _level[polity] = (int)level;
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
        _aggression![polity], _greed![polity], _expansion![polity], _stateSince![polity], _parent![polity]);

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
