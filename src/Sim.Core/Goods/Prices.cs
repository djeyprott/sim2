using Sim.Core.Content;
using Sim.Core.Engine;
using Sim.Core.Map;
using Sim.Core.People;

namespace Sim.Core.Goods;

/// <summary>Price settings, read from prices.json.</summary>
public sealed class PriceParameters
{
    public double Elasticity { get; set; } = 0.7;

    public double MinFactor { get; set; } = 0.2;

    public double MaxFactor { get; set; } = 5.0;

    public double FoodReserveYears { get; set; } = 0.5;

    public double GatherTimeValue { get; set; } = 0.005;

    public static PriceParameters LoadDefault() => Parse(ContentFiles.ReadText("prices.json"));

    public static PriceParameters Parse(string json)
    {
        PriceParameters p = ContentFiles.Parse<PriceParameters>(json, "prices");
        if (p.Elasticity <= 0.0 || p.MinFactor <= 0.0 || p.MaxFactor < 1.0 || p.MinFactor > 1.0
            || p.FoodReserveYears < 0.0 || p.GatherTimeValue < 0.0)
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
    private readonly double[] _wanted;
    private readonly double[] _cached;
    private int _cell = -1;
    private double[]? _cellWanted;

    // For each material, the recipes that use it: (output, units of material per unit of output, share of that output's recipes).
    private readonly List<(int Output, double Amount, double Share)>[] _usedBy;

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
        _wanted = new double[craft.Goods.Count];
        _cached = new double[craft.Goods.Count];
        _usedBy = new List<(int, double, double)>[craft.Goods.Count];
        for (int g = 0; g < _usedBy.Length; g++)
        {
            _usedBy[g] = new List<(int, double, double)>();
        }

        foreach (Recipe recipe in craft.Recipes)
        {
            double share = 1.0 / craft.Recipes.Count(r => r.Output == recipe.Output);
            foreach ((int good, double amount) in recipe.Inputs)
            {
                _usedBy[good].Add((recipe.Output, amount, share));
            }
        }
    }

    public PriceParameters Parameters => _parameters;

    /// <summary>The price from a base value, what is wanted and what is in store.</summary>
    public static double PriceOf(double value, double wanted, double stock, PriceParameters p)
    {
        if (wanted <= 0.0)
        {
            return value * p.MinFactor;
        }

        double ratio = wanted / Math.Max(stock, 1e-6);
        double factor = Math.Pow(ratio, p.Elasticity);
        return value * Math.Clamp(factor, p.MinFactor, p.MaxFactor);
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

        // A year of needs plus the craft reserve.
        for (int g = 0; g < wanted.Length; g++)
        {
            wanted[g] += _craft.NeedPerPerson[g] * fed * (1.0 + _craft.ReserveYears);
        }

        // Materials for what the craftsmen would make: the shortfall of each output, by its recipes.
        for (int g = 0; g < wanted.Length; g++)
        {
            foreach ((int output, double amount, double share) in _usedBy[g])
            {
                double shortfall = Math.Max(0.0, wanted[output] - _stockpiles.Stock(cell, output));
                wanted[g] += shortfall * amount * share;
            }
        }

        if (prices != null)
        {
            for (int g = 0; g < wanted.Length; g++)
            {
                prices[g] = PriceOf(_craft.Goods.Get(g).Value, wanted[g], _stockpiles.Stock(cell, g), _parameters);
            }
        }

        // Prices asked for through Price(good) are worked out lazily from these wanted amounts.
        _cell = cell;
        _cellWanted = wanted;
        Array.Fill(_cached, double.NaN);
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
            price = PriceOf(_craft.Goods.Get(good).Value, _cellWanted![good], _stockpiles.Stock(_cell, good), _parameters);
            _cached[good] = price;
        }

        return price;
    }

    /// <summary>One good's price in a cell's settlement at the given tick.</summary>
    public double Price(int cell, int good, long tick)
    {
        Work(cell, tick, _wanted);
        return PriceOf(_craft.Goods.Get(good).Value, _wanted[good], _stockpiles.Stock(cell, good), _parameters);
    }
}
