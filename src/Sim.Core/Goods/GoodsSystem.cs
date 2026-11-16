using Sim.Core.Engine;
using Sim.Core.People;
using Sim.Core.Settlements;
using Sim.Core.Technologies;

namespace Sim.Core.Goods;

/// <summary>
/// Owns the stockpiles and the ledger of goods (milestone 2), and spoils stored goods once a year
/// at each good's yearly rate. Only settlements hold stock, so only their cells are visited.
/// Grain spoils less where the settlement's farmers use a technology that keeps it (granaries).
/// Other systems produce and consume through <see cref="Stockpiles"/>.
/// </summary>
public sealed class GoodsSystem : ISimSystem
{
    public const string SystemName = "goods";

    private Stockpiles? _stockpiles;
    private EntityTable? _settlements;
    private IntColumn? _settlementCell;
    private TechnologyUse? _technology;
    private TechnologyEffect[] _grainEffects = Array.Empty<TechnologyEffect>();
    private PopulationGroups? _groups;
    private int _grain = -1;
    private int _farmer = -1;

    public GoodsSystem(GoodSet goods)
    {
        Goods = goods;
    }

    public static GoodsSystem CreateDefault() => new GoodsSystem(GoodSet.LoadDefault());

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public GoodSet Goods { get; }

    public void Register(WorldState state) => Stockpiles.Register(state, Goods);

    public void Initialize(SimContext context)
    {
        _stockpiles = new Stockpiles(context.State, Goods);
        _stockpiles.CreateLedger();
        if (context.State.HasTable(SettlementSystem.TableName))
        {
            _settlements = context.State.Table(SettlementSystem.TableName);
            _settlementCell = _settlements.IntColumn("cell");
        }

        _technology = TechnologyUse.TryCreate(context.State, context.Map);
        if (_technology != null && Goods.Goods.Any(g => g.Name == GoodSet.Grain))
        {
            _grainEffects = _technology.WithEffect(TechnologySet.GrainSpoilage);
            _groups = new PopulationGroups(context.State);
            _grain = Goods.IndexOf(GoodSet.Grain);
            _farmer = OccupationSet.LoadDefault().IndexOf(OccupationSet.Farmer);
        }
    }

    public void Update(SimContext context)
    {
        if (_settlements == null)
        {
            return;
        }

        Stockpiles stockpiles = _stockpiles!;
        for (int id = 0; id < _settlements.IdCount; id++)
        {
            if (_settlements.IsAlive(id))
            {
                int cell = _settlementCell![id];
                double grainFactor = _groups == null ? 1.0 : _technology!.Factor(_groups.Find(cell, _farmer), cell, _grainEffects);
                stockpiles.SpoilYear(cell, _grain, grainFactor);
            }
        }
    }
}
