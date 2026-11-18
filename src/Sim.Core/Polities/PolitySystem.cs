using Sim.Core.Content;
using Sim.Core.Engine;
using Sim.Core.Map;
using Sim.Core.Settlements;

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
    double Aggression, double Greed, double Expansion)
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

    public static PolityParameters LoadDefault() => Parse(ContentFiles.ReadText("polities.json"));

    public static PolityParameters Parse(string json)
    {
        PolityParameters p = ContentFiles.Parse<PolityParameters>(json, "polities");
        if (p.TemperamentSpread < 0.0 || p.MinTemperament <= 0.0 || p.MaxTemperament < p.MinTemperament
            || p.Positions.Any(string.IsNullOrWhiteSpace) || p.Positions.Distinct().Count() != p.Positions.Count)
        {
            throw new InvalidDataException("polities.json: temperament spread not negative, 0 < min <= max, and distinct named positions.");
        }

        return p;
    }
}

/// <summary>
/// Polities (milestone 4). Holds the organizations table (every organization, of which polities
/// are the only kind for now), its positions table (empty until named people in milestone 6) and
/// which polity rules each land cell. A polity keeps its row after it ends, as history. Step 1
/// builds the tables and the means to found and rule; polities are founded from step 2.
/// Runs once a year, after seafaring.
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

    private readonly PolityParameters _parameters;
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
    private int[] _settlementPlusOne = Array.Empty<int>();

    public PolitySystem(PolityParameters parameters)
    {
        _parameters = parameters;
    }

    public static PolitySystem CreateDefault() => new PolitySystem(PolityParameters.LoadDefault());

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
        _positions = state.AddTable(PositionTableName);
        _positionOrganization = _positions.AddIntColumn("organization");
        _positionName = _positions.AddStringColumn("position");
        _positionHolder = _positions.AddIntColumn("holder");
    }

    public void Initialize(SimContext context)
    {
        _map = context.Map;
        if (context.State.HasTable(SettlementSystem.TableName))
        {
            _settlementName = context.State.Table(SettlementSystem.TableName).StringColumn("name");
            _settlementPlusOne = context.State.IntField(SettlementSystem.SettlementField);
        }
    }

    public void Update(SimContext context)
    {
        // Founding, growth, tribute, war and collapse come in later steps of milestone 4.
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

    /// <summary>Sets a standing polity's level.</summary>
    public void SetLevel(int polity, PolityLevel level)
    {
        if (!IsStanding(polity))
        {
            throw new ArgumentException($"Polity {polity} does not stand.");
        }

        _level![polity] = (int)level;
    }

    /// <summary>Ends a polity: it rules no cells any more, and its row stays as history.</summary>
    public void End(int polity, long tick)
    {
        if (!IsStanding(polity))
        {
            throw new ArgumentException($"Polity {polity} does not stand.");
        }

        _ended![polity] = (int)tick;
        for (int cell = 0; cell < _rulerPlusOne.Length; cell++)
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
        _aggression![polity], _greed![polity], _expansion![polity]);

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
