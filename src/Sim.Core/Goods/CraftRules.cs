using Sim.Core.Content;

namespace Sim.Core.Goods;

/// <summary>A recipe, with its goods resolved to indexes.</summary>
public sealed class Recipe
{
    public Recipe(int output, double perWorker, (int Good, double Amount)[] inputs)
    {
        Output = output;
        PerWorker = perWorker;
        Inputs = inputs;
    }

    public int Output { get; }

    public double PerWorker { get; }

    /// <summary>Inputs per unit of output, in good order.</summary>
    public (int Good, double Amount)[] Inputs { get; }
}

/// <summary>Recipes, craftsman settings and needs per person, read from recipes.json and needs.json.</summary>
public sealed class CraftRules
{
    private CraftRules(double maxShare, double changeRate, double reserveYears, Recipe[] recipes, double[] needs, GoodSet goods)
    {
        MaxCraftsmanShare = maxShare;
        CraftsmanChangeRate = changeRate;
        ReserveYears = reserveYears;
        Recipes = recipes;
        NeedPerPerson = needs;
        Goods = goods;
        Outputs = recipes.Select(r => r.Output).Distinct().OrderBy(g => g).ToArray();
    }

    public double MaxCraftsmanShare { get; }

    public double CraftsmanChangeRate { get; }

    public double ReserveYears { get; }

    public Recipe[] Recipes { get; }

    /// <summary>Goods that some recipe makes, in good order.</summary>
    public int[] Outputs { get; }

    /// <summary>Units of each good a person uses up in a year (0 for goods that are not needs).</summary>
    public double[] NeedPerPerson { get; }

    public GoodSet Goods { get; }

    public static CraftRules LoadDefault(GoodSet goods) =>
        Parse(ContentFiles.ReadText("recipes.json"), ContentFiles.ReadText("needs.json"), goods);

    public static CraftRules Parse(string recipesJson, string needsJson, GoodSet goods)
    {
        RecipesFile file = ContentFiles.Parse<RecipesFile>(recipesJson, "recipes");
        if (file.MaxCraftsmanShare < 0.0 || file.MaxCraftsmanShare > 0.5 || file.CraftsmanChangeRate < 0.0
            || file.CraftsmanChangeRate > 1.0 || file.ReserveYears < 0.0)
        {
            throw new InvalidDataException("recipes.json: maxCraftsmanShare must be 0 to 0.5, craftsmanChangeRate 0 to 1, reserveYears at least 0.");
        }

        var recipes = new List<Recipe>();
        foreach (RecipeEntry entry in file.Recipes)
        {
            if (entry.PerWorker <= 0.0 || entry.Inputs.Values.Any(a => a < 0.0 || double.IsNaN(a)))
            {
                throw new InvalidDataException($"Recipe for {entry.Output}: perWorker must be above 0 and inputs not negative.");
            }

            (int, double)[] inputs = entry.Inputs
                .Select(pair => (goods.IndexOf(pair.Key), pair.Value))
                .OrderBy(pair => pair.Item1)
                .ToArray();
            recipes.Add(new Recipe(goods.IndexOf(entry.Output), entry.PerWorker, inputs));
        }

        NeedsFile needsFile = ContentFiles.Parse<NeedsFile>(needsJson, "needs");
        var needs = new double[goods.Count];
        foreach (KeyValuePair<string, double> pair in needsFile.Needs)
        {
            int good = goods.IndexOf(pair.Key);
            NeedTier tier = goods.Get(good).NeedTier;
            if (tier != NeedTier.Everyday && tier != NeedTier.Luxury)
            {
                throw new InvalidDataException($"needs.json: {pair.Key} is not an everyday or luxury good.");
            }

            if (pair.Value < 0.0 || double.IsNaN(pair.Value))
            {
                throw new InvalidDataException($"needs.json: the need for {pair.Key} must not be negative.");
            }

            needs[good] = pair.Value;
        }

        return new CraftRules(file.MaxCraftsmanShare, file.CraftsmanChangeRate, file.ReserveYears, recipes.ToArray(), needs, goods);
    }

    private sealed class RecipesFile
    {
        public double MaxCraftsmanShare { get; set; } = 0.1;

        public double CraftsmanChangeRate { get; set; } = 0.2;

        public double ReserveYears { get; set; } = 0.5;

        public List<RecipeEntry> Recipes { get; set; } = new List<RecipeEntry>();
    }

    private sealed class RecipeEntry
    {
        public string Output { get; set; } = "";

        public double PerWorker { get; set; }

        public Dictionary<string, double> Inputs { get; set; } = new Dictionary<string, double>();
    }

    private sealed class NeedsFile
    {
        public Dictionary<string, double> Needs { get; set; } = new Dictionary<string, double>();
    }
}
