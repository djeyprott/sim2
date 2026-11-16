using Sim.Core.Engine;
using Sim.Core.People;
using Sim.Core.Settlements;

namespace Sim.Core.Goods;

/// <summary>
/// Craftsmen, recipes and needs beyond food (milestone 2, steps 3 and 4). Each January, in every
/// settlement: craftsmen move toward the number needed to make what it is short of, counting
/// only work that pays at local prices (what a craftsman makes, less his inputs, must be worth
/// more than the food he eats); they are taken from or given back to the farmers. They make
/// goods by the best-paying recipe from what is in store; then the
/// store-fed people (farmers and craftsmen) use up their everyday and luxury needs, and the
/// share met in each tier is recorded on their groups. Craftsmen left in a cell whose
/// settlement has gone go back to farming.
/// </summary>
public sealed class CraftSystem : ISimSystem
{
    public const string SystemName = "crafts";

    private readonly CraftRules _rules;
    private readonly OccupationSet _occupations;
    private Prices? _prices;
    private readonly double[] _wanted;
    private readonly int _grain;
    private readonly Recipe?[] _best;
    private readonly int _farmer;
    private readonly int _craftsman;
    private Stockpiles? _stockpiles;
    private PopulationGroups? _groups;
    private EntityTable? _settlements;
    private IntColumn? _settlementCell;
    private int[] _settlementPlusOne = Array.Empty<int>();
    private readonly double[] _shortfall;
    private readonly double[] _workers;

    public CraftSystem(CraftRules rules, OccupationSet occupations)
    {
        _rules = rules;
        _occupations = occupations;
        _wanted = new double[rules.Goods.Count];
        _grain = rules.Goods.IndexOf(GoodSet.Grain);
        _best = new Recipe?[rules.Goods.Count];
        _farmer = occupations.IndexOf(OccupationSet.Farmer);
        _craftsman = occupations.IndexOf(OccupationSet.Craftsman);
        _shortfall = new double[rules.Goods.Count];
        _workers = new double[rules.Goods.Count];
    }

    public static CraftSystem CreateDefault(OccupationSet occupations) =>
        new CraftSystem(CraftRules.LoadDefault(GoodSet.LoadDefault()), occupations);

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public CraftRules Rules => _rules;

    public void Register(WorldState state)
    {
    }

    public void Initialize(SimContext context)
    {
        WorldState state = context.State;
        _stockpiles = new Stockpiles(state, _rules.Goods);
        _groups = new PopulationGroups(state);
        _settlements = state.Table(SettlementSystem.TableName);
        _settlementCell = _settlements.IntColumn("cell");
        _settlementPlusOne = state.IntField(SettlementSystem.SettlementField);
        _prices = new Prices(state, context.Map, PriceParameters.LoadDefault(), _rules, _occupations);
    }

    public void Update(SimContext context)
    {
        ReturnStrandedCraftsmen();
        EntityTable settlements = _settlements!;
        for (int id = 0; id < settlements.IdCount; id++)
        {
            if (settlements.IsAlive(id))
            {
                Settlement(_settlementCell![id], context.Tick);
            }
        }
    }

    // Craftsmen in a cell that no longer has a settlement go back to farming.
    private void ReturnStrandedCraftsmen()
    {
        PopulationGroups groups = _groups!;
        for (int group = 0; group < groups.IdCount; group++)
        {
            if (!groups.IsAlive(group) || groups.Occupation(group) != _craftsman || groups.Size(group) <= 0.0)
            {
                continue;
            }

            int cell = groups.Cell(group);
            if (_settlementPlusOne[cell] > 0)
            {
                continue;
            }

            double people = groups.Size(group);
            double food = groups.Food(group);
            double everydayMet = groups.Everyday(group);
            double luxuryMet = groups.Luxury(group);
            int farmers = groups.FindOrAdd(cell, _farmer);
            groups.SetSize(group, 0.0);
            groups.AddPeople(farmers, people, food, everydayMet, luxuryMet, group);
        }
    }

    private void Settlement(int cell, long tick)
    {
        PopulationGroups groups = _groups!;
        Stockpiles stockpiles = _stockpiles!;
        int farmerGroup = groups.Find(cell, _farmer);
        double farmers = farmerGroup >= 0 ? groups.Size(farmerGroup) : 0.0;
        int craftsmanGroup = groups.Find(cell, _craftsman);
        double craftsmen = craftsmanGroup >= 0 ? groups.Size(craftsmanGroup) : 0.0;
        double fed = farmers + craftsmen;
        if (fed <= 0.0)
        {
            return;
        }

        // What the people are short of, and the craftsmen it would take to make it, counting only
        // work that pays: a year of a craftsman's output, less its inputs at local prices, must be
        // worth more than the grain he eats.
        _prices!.Work(cell, tick, _wanted);
        double grainPrice = _prices.Price(_grain);
        double workersWanted = 0.0;
        foreach (int output in _rules.Outputs)
        {
            _shortfall[output] = Math.Max(0.0, _wanted[output] - stockpiles.Stock(cell, output));
            if (_shortfall[output] <= 0.0)
            {
                _workers[output] = 0.0;
                continue;
            }

            _best[output] = BestRecipe(output, out double workerValue);
            _workers[output] = _best[output] != null && workerValue > grainPrice ? _shortfall[output] / _best[output]!.PerWorker : 0.0;
            workersWanted += _workers[output];
        }

        // Craftsmen move toward that number, taken from the farmers or given back to them.
        double target = Math.Min(workersWanted, _rules.MaxCraftsmanShare * fed);
        double change = _rules.CraftsmanChangeRate * (target - craftsmen);
        if (change > 0.0 && farmerGroup >= 0)
        {
            change = Math.Min(change, 0.5 * farmers);
            double food = groups.Food(farmerGroup);
            double everydayMet = groups.Everyday(farmerGroup);
            double luxuryMet = groups.Luxury(farmerGroup);
            craftsmanGroup = groups.FindOrAdd(cell, _craftsman);
            farmerGroup = groups.Find(cell, _farmer);
            groups.SetSize(farmerGroup, farmers - change);
            groups.AddPeople(craftsmanGroup, change, food, everydayMet, luxuryMet, farmerGroup);
            craftsmen += change;
        }
        else if (change < 0.0 && craftsmanGroup >= 0)
        {
            double leaving = Math.Min(-change, craftsmen);
            double food = groups.Food(craftsmanGroup);
            double everydayMet = groups.Everyday(craftsmanGroup);
            double luxuryMet = groups.Luxury(craftsmanGroup);
            groups.SetSize(craftsmanGroup, craftsmen - leaving);
            farmerGroup = groups.FindOrAdd(cell, _farmer);
            groups.AddPeople(farmerGroup, leaving, food, everydayMet, luxuryMet, craftsmanGroup);
            craftsmen -= leaving;
        }

        // The craftsmen make goods, shared between outputs by how much work each shortage takes.
        if (craftsmen > 0.0 && workersWanted > 0.0)
        {
            double share = Math.Min(1.0, craftsmen / workersWanted);
            foreach (int output in _rules.Outputs)
            {
                double work = _workers[output] * share;
                foreach (Recipe recipe in RecipesFor(output))
                {
                    if (work <= 0.0)
                    {
                        break;
                    }

                    double units = work * recipe.PerWorker;
                    foreach ((int good, double amount) in recipe.Inputs)
                    {
                        if (amount > 0.0)
                        {
                            units = Math.Min(units, stockpiles.Stock(cell, good) / amount);
                        }
                    }

                    if (units <= 0.0)
                    {
                        continue;
                    }

                    foreach ((int good, double amount) in recipe.Inputs)
                    {
                        stockpiles.Consume(cell, good, units * amount);
                    }

                    stockpiles.Produce(cell, output, units);
                    work -= units / recipe.PerWorker;
                }
            }
        }

        // The store-fed people use up their everyday and luxury needs. The dearer a good is here, the
        // less of it they use (its base value over its price, at most all of the need), so a scarce
        // good is not all used up by the first village it reaches and can be traded further.
        // Where better goods meet a need (metal tools for stone), they use the best first, each by
        // its own price, and the lesser ones for the rest.
        _prices.Work(cell, tick, _wanted);
        double everydayWanted = 0.0, everydayTaken = 0.0, luxuryWanted = 0.0, luxuryTaken = 0.0;
        for (int need = 0; need < _rules.NeedPerPerson.Length; need++)
        {
            double perPerson = _rules.NeedPerPerson[need];
            if (perPerson <= 0.0)
            {
                continue;
            }

            double wanted = perPerson * fed;
            double remaining = wanted;
            foreach (int good in _rules.NeedGoods[need])
            {
                double counts = _rules.Counts(good);
                double use = Math.Min(1.0, Prices.Power(_rules.Goods.Get(good).Value / _prices.Price(good), _prices.Parameters.NeedElasticity));
                remaining -= counts * stockpiles.Consume(cell, good, remaining / counts * use);
            }

            double taken = wanted - remaining;
            if (_rules.Goods.Get(need).NeedTier == NeedTier.Everyday)
            {
                everydayWanted += wanted;
                everydayTaken += taken;
            }
            else
            {
                luxuryWanted += wanted;
                luxuryTaken += taken;
            }
        }

        double everyday = everydayWanted > 0.0 ? everydayTaken / everydayWanted : 1.0;
        double luxury = luxuryWanted > 0.0 ? luxuryTaken / luxuryWanted : 1.0;
        farmerGroup = groups.Find(cell, _farmer);
        craftsmanGroup = groups.Find(cell, _craftsman);
        if (farmerGroup >= 0)
        {
            groups.SetNeedsMet(farmerGroup, everyday, luxury);
        }

        if (craftsmanGroup >= 0)
        {
            groups.SetNeedsMet(craftsmanGroup, everyday, luxury);
        }
    }

    // The recipe for an output whose year of work is worth most at local prices, and that worth,
    // among those the settlement knows how to follow (null if none).
    private Recipe? BestRecipe(int output, out double workerValue)
    {
        Recipe? best = null;
        workerValue = double.NegativeInfinity;
        foreach (Recipe recipe in _rules.Recipes)
        {
            if (recipe.Output != output || !_prices!.Allowed(recipe))
            {
                continue;
            }

            double margin = _prices!.Price(output);
            foreach ((int good, double amount) in recipe.Inputs)
            {
                margin -= _prices.Price(good) * amount;
            }

            double value = margin * recipe.PerWorker;
            if (value > workerValue)
            {
                workerValue = value;
                best = recipe;
            }
        }

        return best;
    }

    // An output's recipes the settlement can follow, the best-paying first, then the rest in file order.
    private IEnumerable<Recipe> RecipesFor(int output)
    {
        Recipe? best = _best[output];
        if (best == null)
        {
            yield break;
        }

        yield return best;
        foreach (Recipe recipe in _rules.Recipes)
        {
            if (recipe.Output == output && !ReferenceEquals(recipe, best) && _prices!.Allowed(recipe))
            {
                yield return recipe;
            }
        }
    }
}
