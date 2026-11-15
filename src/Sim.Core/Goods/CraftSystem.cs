using Sim.Core.Engine;
using Sim.Core.People;
using Sim.Core.Settlements;

namespace Sim.Core.Goods;

/// <summary>
/// Craftsmen, recipes and needs beyond food (milestone 2, step 3). Each January, in every
/// settlement: craftsmen move toward the number its people's shortages call for (taken from
/// or given back to the farmers); they make goods by recipe from what is in store; then the
/// store-fed people (farmers and craftsmen) use up their everyday and luxury needs, and the
/// share met in each tier is recorded on their groups. Craftsmen left in a cell whose
/// settlement has gone go back to farming.
/// </summary>
public sealed class CraftSystem : ISimSystem
{
    public const string SystemName = "crafts";

    private readonly CraftRules _rules;
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
    }

    public void Update(SimContext context)
    {
        ReturnStrandedCraftsmen();
        EntityTable settlements = _settlements!;
        for (int id = 0; id < settlements.IdCount; id++)
        {
            if (settlements.IsAlive(id))
            {
                Settlement(_settlementCell![id]);
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
            groups.SetSize(group, 0.0);
            groups.AddPeople(groups.FindOrAdd(cell, _farmer), people, food, everydayMet, luxuryMet);
        }
    }

    private void Settlement(int cell)
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

        // What the people are short of, and the craftsmen that would take to make it.
        double workersWanted = 0.0;
        foreach (int output in _rules.Outputs)
        {
            double wanted = _rules.NeedPerPerson[output] * fed * (1.0 + _rules.ReserveYears) - stockpiles.Stock(cell, output);
            _shortfall[output] = Math.Max(0.0, wanted);
            Recipe first = FirstRecipe(output);
            _workers[output] = _shortfall[output] / first.PerWorker;
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
            groups.AddPeople(craftsmanGroup, change, food, everydayMet, luxuryMet);
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
            groups.AddPeople(farmerGroup, leaving, food, everydayMet, luxuryMet);
            craftsmen -= leaving;
        }

        // The craftsmen make goods, shared between outputs by how much work each shortage takes.
        if (craftsmen > 0.0 && workersWanted > 0.0)
        {
            double share = Math.Min(1.0, craftsmen / workersWanted);
            foreach (int output in _rules.Outputs)
            {
                double work = _workers[output] * share;
                foreach (Recipe recipe in _rules.Recipes)
                {
                    if (recipe.Output != output || work <= 0.0)
                    {
                        continue;
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

        // The store-fed people use up their everyday and luxury needs.
        double everydayWanted = 0.0, everydayTaken = 0.0, luxuryWanted = 0.0, luxuryTaken = 0.0;
        for (int good = 0; good < _rules.NeedPerPerson.Length; good++)
        {
            double perPerson = _rules.NeedPerPerson[good];
            if (perPerson <= 0.0)
            {
                continue;
            }

            double wanted = perPerson * fed;
            double taken = stockpiles.Consume(cell, good, wanted);
            if (_rules.Goods.Get(good).NeedTier == NeedTier.Everyday)
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

    private Recipe FirstRecipe(int output)
    {
        foreach (Recipe recipe in _rules.Recipes)
        {
            if (recipe.Output == output)
            {
                return recipe;
            }
        }

        throw new InvalidOperationException("Every output has a recipe.");
    }
}
