using Sim.Core.Engine;

namespace Sim.Core.Goods;

/// <summary>
/// Settlement stockpiles and the world ledger of goods (milestone 2). A stockpile is one per-cell
/// field per good ("goods.stock.&lt;good&gt;"); only cells with a settlement hold stock. The ledger is a
/// table with one row per good counting everything produced, consumed, spoiled and lost since the
/// run began, so produced always equals consumed + spoiled + lost + in store + in transit.
/// Registered by <see cref="GoodsSystem"/>; other systems use this class to read and change stock.
/// </summary>
public sealed class Stockpiles
{
    public const string StockFieldPrefix = "goods.stock.";
    public const string LedgerTableName = "goods.ledger";

    private readonly GoodSet _goods;
    private readonly double[][] _stock;
    private readonly EntityTable _ledger;
    private readonly DoubleColumn _produced;
    private readonly DoubleColumn _consumed;
    private readonly DoubleColumn _spoiled;
    private readonly DoubleColumn _lost;
    private readonly DoubleColumn _transit;

    public Stockpiles(WorldState state, GoodSet goods)
    {
        _goods = goods;
        _stock = goods.Goods.Select(g => state.DoubleField(StockFieldPrefix + g.Slug)).ToArray();
        _ledger = state.Table(LedgerTableName);
        _produced = _ledger.DoubleColumn("produced");
        _consumed = _ledger.DoubleColumn("consumed");
        _spoiled = _ledger.DoubleColumn("spoiled");
        _lost = _ledger.DoubleColumn("lost");
        _transit = _ledger.DoubleColumn("transit");
    }

    public GoodSet Goods => _goods;

    public static void Register(WorldState state, GoodSet goods)
    {
        foreach (Good good in goods.Goods)
        {
            state.AddDoubleField(StockFieldPrefix + good.Slug);
        }

        EntityTable ledger = state.AddTable(LedgerTableName);
        ledger.AddDoubleColumn("produced");
        ledger.AddDoubleColumn("consumed");
        ledger.AddDoubleColumn("spoiled");
        ledger.AddDoubleColumn("lost");
        ledger.AddDoubleColumn("transit");
    }

    /// <summary>Adds one ledger row per good. Called once, by the goods system, for a new run.</summary>
    internal void CreateLedger()
    {
        for (int i = 0; i < _goods.Count; i++)
        {
            int row = _ledger.Add();
            if (row != i)
            {
                throw new InvalidOperationException("The goods ledger must have one row per good, in order.");
            }
        }
    }

    /// <summary>The per-cell stock array of a good. Read only; change stock through the methods below.</summary>
    public double[] StockField(int good) => _stock[good];

    public double Stock(int cell, int good) => _stock[good][cell];

    /// <summary>Adds newly made goods to a cell's stockpile.</summary>
    public void Produce(int cell, int good, double amount)
    {
        if (amount <= 0.0)
        {
            return;
        }

        _stock[good][cell] += amount;
        _produced[good] += amount;
    }

    /// <summary>Takes up to the amount from a cell's stockpile to be used up. Returns what was taken.</summary>
    public double Consume(int cell, int good, double amount)
    {
        double taken = Math.Min(Math.Max(amount, 0.0), _stock[good][cell]);
        _stock[good][cell] -= taken;
        _consumed[good] += taken;
        return taken;
    }

    /// <summary>Records goods made and used at once, outside any stockpile (for example foragers' wild food).</summary>
    public void ProduceAndConsume(int good, double produced, double consumed)
    {
        produced = Math.Max(produced, 0.0);
        consumed = Math.Clamp(consumed, 0.0, produced);
        _produced[good] += produced;
        _consumed[good] += consumed;
        _lost[good] += produced - consumed;
    }

    /// <summary>Spoils one year's share of every stock in a cell.</summary>
    public void SpoilYear(int cell)
    {
        for (int g = 0; g < _stock.Length; g++)
        {
            double stock = _stock[g][cell];
            if (stock > 0.0)
            {
                double spoiled = stock * _goods.Get(g).Spoilage;
                _stock[g][cell] = stock - spoiled;
                _spoiled[g] += spoiled;
            }
        }
    }

    /// <summary>Everything in a cell's stockpile is lost, for example when its settlement is abandoned.</summary>
    public void LoseAll(int cell)
    {
        for (int g = 0; g < _stock.Length; g++)
        {
            double stock = _stock[g][cell];
            if (stock != 0.0)
            {
                _lost[g] += stock;
                _stock[g][cell] = 0.0;
            }
        }
    }

    public double Produced(int good) => _produced[good];

    public double Consumed(int good) => _consumed[good];

    public double Spoiled(int good) => _spoiled[good];

    public double Lost(int good) => _lost[good];

    public double InTransit(int good) => _transit[good];

    /// <summary>The world's total stock of a good.</summary>
    public double InStore(int good)
    {
        double total = 0.0;
        foreach (double v in _stock[good])
        {
            total += v;
        }

        return total;
    }

    /// <summary>Produced minus everything it went to, as a share of produced (0 when the books balance).</summary>
    public double Imbalance(int good)
    {
        double produced = _produced[good];
        double accounted = _consumed[good] + _spoiled[good] + _lost[good] + _transit[good] + InStore(good);
        return produced <= 0.0 ? Math.Abs(accounted) : Math.Abs(produced - accounted) / produced;
    }
}
