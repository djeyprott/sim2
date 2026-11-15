using Sim.Core.Engine;
using Sim.Core.Goods;
using Sim.Core.Map;
using Sim.Core.People;

namespace Sim.Core.Settlements;

/// <summary>A settlement as the viewer sees it.</summary>
public readonly record struct SettlementInfo(int Id, int Cell, string Name, long Founded, double Population);

/// <summary>
/// Settlements (milestone 1, step 9). A cell whose farmers reach a threshold founds one named
/// settlement, which stands for its main village or town. A share of the cell's people live in
/// it, so it grows and shrinks with the cell. It is abandoned when the cell's farmers fall below
/// a lower threshold. Settlements are rows in a table whose ids are never reused.
/// </summary>
public sealed class SettlementSystem : ISimSystem
{
    public const string SystemName = "settlements";
    public const string TableName = "settlements";

    /// <summary>Settlement id plus one in each cell (0: none).</summary>
    public const string SettlementField = "settlements.id";

    private readonly SettlementParameters _parameters;
    private readonly int _farmer;
    private int[] _settlementPlusOne = Array.Empty<int>();
    private EntityTable? _table;
    private IntColumn? _cell;
    private IntColumn? _founded;
    private DoubleColumn? _population;
    private StringColumn? _name;
    private PopulationGroups? _groups;
    private Stockpiles? _stockpiles;
    private int[] _land = Array.Empty<int>();
    private int[] _farmerGroup = Array.Empty<int>();

    public SettlementSystem(SettlementParameters parameters, OccupationSet occupations)
    {
        _parameters = parameters;
        _farmer = occupations.IndexOf(OccupationSet.Farmer);
    }

    public static SettlementSystem CreateDefault(OccupationSet occupations) =>
        new SettlementSystem(SettlementParameters.LoadDefault(), occupations);

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public SettlementParameters Parameters => _parameters;

    public void Register(WorldState state)
    {
        _settlementPlusOne = state.AddIntField(SettlementField);
        _table = state.AddTable(TableName);
        _cell = _table.AddIntColumn("cell");
        _founded = _table.AddIntColumn("founded");
        _population = _table.AddDoubleColumn("population");
        _name = _table.AddStringColumn("name");
    }

    public void Initialize(SimContext context)
    {
        WorldMap map = context.Map;
        _groups = new PopulationGroups(context.State);
        if (context.State.HasTable(Stockpiles.LedgerTableName))
        {
            _stockpiles = new Stockpiles(context.State, GoodSet.LoadDefault());
        }
        _land = Enumerable.Range(0, map.CellCount).Where(map.IsLand).ToArray();
        _farmerGroup = new int[map.CellCount];
    }

    public void Update(SimContext context)
    {
        PopulationGroups groups = _groups!;
        groups.FillGroupIndex(_farmerGroup, _farmer);
        foreach (int cell in _land)
        {
            int group = _farmerGroup[cell];
            double farmers = group >= 0 ? groups.Size(group) : 0.0;
            int id = _settlementPlusOne[cell] - 1;
            if (id < 0)
            {
                if (farmers >= _parameters.FoundAtFarmers)
                {
                    id = Found(cell, context.Tick, context.Rng);
                }
                else
                {
                    continue;
                }
            }
            else if (farmers < _parameters.AbandonBelowFarmers)
            {
                _table!.Remove(id);
                _settlementPlusOne[cell] = 0;
                _stockpiles?.LoseAll(cell);
                continue;
            }

            _population![id] = _parameters.SettledShare * groups.CellTotal(cell);
        }
    }

    /// <summary>Number of settlements standing now.</summary>
    public int Count => _table?.LiveCount ?? 0;

    /// <summary>The settlement in a cell, or null.</summary>
    public SettlementInfo? InCell(int cell)
    {
        int id = _settlementPlusOne[cell] - 1;
        return id >= 0 ? Info(id) : null;
    }

    /// <summary>Every standing settlement, in id order.</summary>
    public List<SettlementInfo> All()
    {
        var list = new List<SettlementInfo>(Count);
        EntityTable table = _table!;
        for (int id = 0; id < table.IdCount; id++)
        {
            if (table.IsAlive(id))
            {
                list.Add(Info(id));
            }
        }

        return list;
    }

    private SettlementInfo Info(int id) =>
        new SettlementInfo(id, _cell![id], _name![id] ?? "", _founded![id], _population![id]);

    private int Found(int cell, long tick, Rng rng)
    {
        int id = _table!.Add();
        _cell![id] = cell;
        _founded![id] = (int)tick;
        _name![id] = _parameters.MakeName(rng);
        _settlementPlusOne[cell] = id + 1;
        return id;
    }
}
