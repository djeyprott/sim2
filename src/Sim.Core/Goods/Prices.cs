using Sim.Core.Content;
using Sim.Core.Engine;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Technologies;

namespace Sim.Core.Goods;

/// <summary>Price settings, read from prices.json.</summary>
public sealed class PriceParameters
{
    public double Elasticity { get; set; } = 0.7;

    public double MinFactor { get; set; } = 0.2;

    public double MaxFactor { get; set; } = 5.0;

    /// <summary>The highest price of a luxury, as a multiple of its value (rare goods far from their source).</summary>
    public double LuxuryMaxFactor { get; set; } = 5.0;

    public double FoodReserveYears { get; set; } = 0.5;

    public double GatherTimeValue { get; set; } = 0.005;

    public double SalesWanted { get; set; } = 1.5;

    public double NeedElasticity { get; set; } = 1.0;

    public static PriceParameters LoadDefault() => Parse(ContentFiles.ReadText("prices.json"));

    public static PriceParameters Parse(string json)
    {
        PriceParameters p = ContentFiles.Parse<PriceParameters>(json, "prices");
        if (p.Elasticity <= 0.0 || p.MinFactor <= 0.0 || p.MaxFactor < 1.0 || p.MinFactor > 1.0
            || p.FoodReserveYears < 0.0 || p.GatherTimeValue < 0.0 || p.SalesWanted < 0.0 || p.NeedElasticity < 0.0 || p.LuxuryMaxFactor < 1.0)
        {
            throw new InvalidDataException("prices.json: elasticity above 0, 0 < minFactor <= 1 <= maxFactor, and no negative reserve or time value.");
        }

        return p;
    }
}

/// <summary>
/// Local prices in food (milestone 2, step 4), worked out when needed from a settlement's stock
/// and what it wants; never stored. Prices only make sense where there is a settlement, since
/// only settlements hold stock. Read-only: it never changes the world state.
/// </summary>
public sealed class Prices
{
    private const int NorthernHarvestMonth = 9;
    private const int SouthernHarvestMonth = 3;

    private readonly PriceParameters _parameters;
    private readonly CraftRules _craft;
    private readonly Stockpiles _stockpiles;
    private readonly PopulationGroups _groups;
    private readonly WorldMap _map;
    private readonly int _farmer;
    private readonly int _craftsman;
    private readonly int _grain;
    private readonly int[] _seasonalGoods;
    private readonly double[] _wanted;
    private readonly double[] _cached;
    private int _cell = -1;
    private double[]? _cellWanted;

    // For each material, the recipes that use it: (output, units of material per unit of output, share of that output's recipes, recipe).
    private readonly List<(int Output, double Amount, double Share, int Recipe)>[] _usedBy;

    // Per recipe: the technology it needs (-1: none; -2: one this simulation does not have), and
    // whether the settlement last passed to Work may follow it.
    private readonly TechnologyUse? _technology;
    private readonly int[] _recipeTechnology;
    private readonly bool[] _allowed;
    private readonly sbyte[] _known;

    public Prices(WorldState state, WorldMap map, PriceParameters parameters, CraftRules craft, OccupationSet occupations)
    {
        _parameters = parameters;
        _craft = craft;
        _map = map;
        _stockpiles = new Stockpiles(state, craft.Goods);
        _groups = new PopulationGroups(state);
        _farmer = occupations.IndexOf(OccupationSet.Farmer);
        _craftsman = occupations.Contains(OccupationSet.Craftsman) ? occupations.IndexOf(OccupationSet.Craftsman) : -1;
        _grain = craft.Goods.IndexOf(GoodSet.Grain);
        _seasonalGoods = craft.Goods.Goods.Where(g => g.Food || g.NeedTier == NeedTier.Luxury).Select(g => g.Index).ToArray();
        _wanted = new double[craft.Goods.Count];
        _cached = new double[craft.Goods.Count];
        _usedBy = new List<(int, double, double, int)>[craft.Goods.Count];
        for (int g = 0; g < _usedBy.Length; g++)
        {
            _usedBy[g] = new List<(int, double, double, int)>();
        }

        for (int r = 0; r < craft.Recipes.Length; r++)
        {
            Recipe recipe = craft.Recipes[r];
            double share = 1.0 / craft.Recipes.Count(other => other.Output == recipe.Output);
            foreach ((int good, double amount) in recipe.Inputs)
            {
                _usedBy[good].Add((recipe.Output, amount, share, r));
            }
        }

        _technology = craft.Recipes.Any(r => r.Requires.Length > 0) ? TechnologyUse.TryCreate(state, map) : null;
        _recipeTechnology = craft.Recipes
            .Select(r => r.Requires.Length == 0 ? -1 : _technology == null ? -2 : _technology.Technologies.IndexOf(r.Requires))
            .ToArray();
        _allowed = new bool[craft.Recipes.Length];
        _known = new sbyte[_technology?.Technologies.Count ?? 0];

        if (_seasonalGoods.Any(g => _usedBy[g].Count > 0))
        {
            throw new InvalidDataException("Food and luxuries must not be recipe inputs: trade prices them between Januaries without recipes.");
        }
    }

    public PriceParameters Parameters => _parameters;

    /// <summary>Whether the settlement last passed to <see cref="Work"/> may follow a recipe (it uses the technology the recipe needs).</summary>
    public bool Allowed(Recipe recipe)
    {
        for (int r = 0; r < _allowed.Length; r++)
        {
            if (ReferenceEquals(_craft.Recipes[r], recipe))
            {
                return _allowed[r];
            }
        }

        return false;
    }

    /// <summary>The price from a base value, what is wanted and what is in store.</summary>
    public static double PriceOf(double value, double wanted, double stock, PriceParameters p) =>
        PriceOf(value, wanted, stock, p, p.MaxFactor);

    /// <summary>
    /// x to the power e, without calling Math.Pow for the common powers 1, 2 and 0.5: on some build
    /// machines Math.Pow is ten times slower than on others, and these run hundreds of millions of times.
    /// </summary>
    public static double Power(double x, double e) =>
        e == 1.0 ? x : e == 2.0 ? x * x : e == 0.5 ? Math.Sqrt(x) : Math.Pow(x, e);

    /// <summary>The highest price of a good as a multiple of its value.</summary>
    public static double MaxFactorOf(Good good, PriceParameters p) =>
        good.NeedTier == NeedTier.Luxury ? p.LuxuryMaxFactor : p.MaxFactor;

    /// <summary>The price from a base value, what is wanted and what is in store, with its own ceiling.</summary>
    public static double PriceOf(double value, double wanted, double stock, PriceParameters p, double maxFactor)
    {
        if (wanted <= 0.0)
        {
            return value * p.MinFactor;
        }

        double ratio = wanted / Math.Max(stock, 1e-6);
        double factor = Power(ratio, p.Elasticity);
        return value * Math.Clamp(factor, p.MinFactor, maxFactor);
    }

    /// <summary>The settlement's store-fed people: its farmers and craftsmen.</summary>
    public double StoreFed(int cell)
    {
        int farmer = _groups.Find(cell, _farmer);
        double people = farmer >= 0 ? _groups.Size(farmer) : 0.0;
        if (_craftsman >= 0)
        {
            int craftsman = _groups.Find(cell, _craftsman);
            people += craftsman >= 0 ? _groups.Size(craftsman) : 0.0;
        }

        return people;
    }

    /// <summary>
    /// Fills <paramref name="wanted"/> with what the settlement in a cell wants of each good, at
    /// the given tick, and <paramref name="prices"/> (if given) with its prices.
    /// </summary>
    public void Work(int cell, long tick, double[] wanted, double[]? prices = null)
    {
        double fed = StoreFed(cell);
        Array.Clear(wanted);

        // Food until the next harvest, plus a reserve.
        int month = (int)(tick % GameDate.MonthsPerYear);
        int harvest = _map.Grid.Latitude(cell) >= 0.0 ? NorthernHarvestMonth : SouthernHarvestMonth;
        int months = (harvest - month + GameDate.MonthsPerYear) % GameDate.MonthsPerYear;
        wanted[_grain] = fed * (months / (double)GameDate.MonthsPerYear + _parameters.FoodReserveYears);

        // Which recipes the settlement's people know how to follow (each technology looked up once).
        Array.Fill(_known, (sbyte)-1);
        for (int r = 0; r < _allowed.Length; r++)
        {
            int technology = _recipeTechnology[r];
            if (technology >= 0 && _known[technology] < 0)
            {
                _known[technology] = _technology!.KnownInCell(cell, technology) ? (sbyte)1 : (sbyte)0;
            }

            _allowed[r] = technology == -1 || (technology >= 0 && _known[technology] == 1);
        }

        // A year of needs plus the craft reserve. Where better goods meet a need (metal tools for
        // stone), the best is wanted for the whole need, and each lesser one for what the better
        // ones in store do not already cover.
        for (int g = 0; g < wanted.Length; g++)
        {
            double need = _craft.NeedPerPerson[g] * fed * (1.0 + _craft.ReserveYears);
            if (need <= 0.0)
            {
                continue;
            }

            foreach (int good in _craft.NeedGoods[g])
            {
                double counts = _craft.Counts(good);
                wanted[good] += need / counts;
                need = Math.Max(0.0, need - _stockpiles.Stock(cell, good) * counts);
            }
        }

        // Stock to keep selling what it has lately sold to its trade partners (not food: farmers
        // grow for themselves, and selling grain should not make a settlement hoard it).
        for (int g = 0; g < wanted.Length; g++)
        {
            if (!_craft.Goods.Get(g).Food)
            {
                wanted[g] += _parameters.SalesWanted * _stockpiles.Sold(cell, g);
            }
        }

        // Materials for what the craftsmen would make: the shortfall of each output, by the recipes
        // they can follow. Outputs come after their inputs in the goods list, so going backwards
        // gives each output its full want (copper for copper tools) before its inputs (copper ore).
        for (int g = wanted.Length - 1; g >= 0; g--)
        {
            foreach ((int output, double amount, double share, int recipe) in _usedBy[g])
            {
                if (!_allowed[recipe])
                {
                    continue;
                }

                double shortfall = Math.Max(0.0, wanted[output] - _stockpiles.Stock(cell, output));
                wanted[g] += shortfall * amount * share;
            }
        }

        if (prices != null)
        {
            for (int g = 0; g < wanted.Length; g++)
            {
                prices[g] = PriceOf(_craft.Goods.Get(g).Value, wanted[g], _stockpiles.Stock(cell, g), _parameters, MaxFactorOf(_craft.Goods.Get(g), _parameters));
            }
        }

        // Prices asked for through Price(good) are worked out lazily from these wanted amounts.
        _cell = cell;
        _cellWanted = wanted;
        Array.Fill(_cached, double.NaN);
    }

    /// <summary>
    /// Fills <paramref name="prices"/> for food and luxuries only (others are left as they are), as
    /// <see cref="Work"/> would. Neither is used in recipes, so what is wanted needs no recipe
    /// shortfalls: food until the next harvest plus a reserve, needs, and for luxuries what was sold
    /// lately. Much cheaper than Work, for trade's seasons between Januaries.
    /// </summary>
    public void WorkSeasonal(int cell, long tick, double[] prices)
    {
        double fed = StoreFed(cell);
        int month = (int)(tick % GameDate.MonthsPerYear);
        int harvest = _map.Grid.Latitude(cell) >= 0.0 ? NorthernHarvestMonth : SouthernHarvestMonth;
        int months = (harvest - month + GameDate.MonthsPerYear) % GameDate.MonthsPerYear;
        foreach (int g in _seasonalGoods)
        {
            Good good = _craft.Goods.Get(g);
            double wanted = _craft.NeedPerPerson[g] * fed * (1.0 + _craft.ReserveYears);
            if (g == _grain)
            {
                wanted += fed * (months / (double)GameDate.MonthsPerYear + _parameters.FoodReserveYears);
            }

            if (!good.Food)
            {
                wanted += _parameters.SalesWanted * _stockpiles.Sold(cell, g);
            }

            prices[g] = PriceOf(good.Value, wanted, _stockpiles.Stock(cell, g), _parameters, MaxFactorOf(good, _parameters));
        }
    }

    /// <summary>
    /// A good's price in the settlement last passed to <see cref="Work"/>, from its stock now.
    /// Worked out on first use and kept until the next call to Work, so call Work again after the
    /// settlement's stock changes.
    /// </summary>
    public double Price(int good)
    {
        double price = _cached[good];
        if (double.IsNaN(price))
        {
            price = PriceOf(_craft.Goods.Get(good).Value, _cellWanted![good], _stockpiles.Stock(_cell, good), _parameters, MaxFactorOf(_craft.Goods.Get(good), _parameters));
            _cached[good] = price;
        }

        return price;
    }

    /// <summary>A good's price in the settlement last passed to <see cref="Work"/>, counting goods on their way there as in store.</summary>
    public double PriceWithIncoming(int good) =>
        PriceOf(_craft.Goods.Get(good).Value, _cellWanted![good], _stockpiles.Stock(_cell, good) + _stockpiles.Incoming(_cell, good), _parameters, MaxFactorOf(_craft.Goods.Get(good), _parameters));

    /// <summary>One good's price in a cell's settlement at the given tick.</summary>
    public double Price(int cell, int good, long tick)
    {
        Work(cell, tick, _wanted);
        return PriceOf(_craft.Goods.Get(good).Value, _wanted[good], _stockpiles.Stock(cell, good), _parameters, MaxFactorOf(_craft.Goods.Get(good), _parameters));
    }
}
