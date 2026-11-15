using Sim.Core.Engine;
using Sim.Core.Settlements;

namespace Sim.Core.Goods;

/// <summary>
/// Owns the stockpiles and the ledger of goods (milestone 2), and spoils stored goods once a year
/// at each good's yearly rate. Only settlements hold stock, so only their cells are visited.
/// Other systems produce and consume through <see cref="Stockpiles"/>.
/// </summary>
public sealed class GoodsSystem : ISimSystem
{
    public const string SystemName = "goods";

    private Stockpiles? _stockpiles;
    private EntityTable? _settlements;
    private IntColumn? _settlementCell;

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
                stockpiles.SpoilYear(_settlementCell![id]);
            }
        }
    }
}
