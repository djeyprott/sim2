using Sim.Core.Content;

namespace Sim.Core.Goods;

/// <summary>A recipe, with its goods resolved to indexes.</summary>
public sealed class Recipe
{
    public Recipe(int output, double perWorker, (int Good, double Amount)[] inputs, string requires = "")
    {
        Output = output;
        PerWorker = perWorker;
        Inputs = inputs;
        Requires = requires;
    }

    /// <summary>The technology (by name) a settlement must use to follow it; empty for none.</summary>
    public string Requires { get; }

    public int Output { get; }

    public double PerWorker { get; }

    /// <summary>Inputs per unit of output, in good order.</summary>
    public (int Good, double Amount)[] Inputs { get; }
}

/// <summary>Recipes, craftsman settings and needs per person, read from recipes.json and needs.json.</summary>
public sealed class CraftRules
{
    private CraftRules(double maxShare, double changeRate, double reserveYears, Recipe[] recipes, double[] needs, GoodSet goods,
        int[] substituteFor, double[] substituteCounts)
    {
        SubstituteFor = substituteFor;
        SubstituteCounts = substituteCounts;
        // For each need: the goods that meet it, best first (most counted per unit), the good itself last.
        NeedGoods = new int[goods.Count][];
        for (int g = 0; g < goods.Count; g++)
        {
            NeedGoods[g] = needs[g] <= 0.0 ? Array.Empty<int>() : Enumerable.Range(0, goods.Count)
                .Where(s => substituteFor[s] == g)
                .OrderByDescending(s => substituteCounts[s]).ThenBy(s => s)
                .Append(g).ToArray();
        }

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

    /// <summary>By good: the need it meets in place of another good (-1: none), and how many units of that good one unit counts as.</summary>
    public int[] SubstituteFor { get; }

    public double[] SubstituteCounts { get; }

    /// <summary>By need good: the goods that meet it, best first, the good itself last (empty for goods that are not needs).</summary>
    public int[][] NeedGoods { get; }

    /// <summary>How many units of the need one unit of a good meets (1 for the need's own good).</summary>
    public double Counts(int good) => SubstituteFor[good] >= 0 ? SubstituteCounts[good] : 1.0;

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
            int output = goods.IndexOf(entry.Output);
            if (inputs.Any(i => i.Item1 >= output))
            {
                throw new InvalidDataException($"Recipe for {entry.Output}: goods.json must list every input before the output.");
            }

            recipes.Add(new Recipe(output, entry.PerWorker, inputs, entry.Requires));
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

        var substituteFor = Enumerable.Repeat(-1, goods.Count).ToArray();
        var substituteCounts = new double[goods.Count];
        foreach (KeyValuePair<string, SubstituteEntry> pair in needsFile.Substitutes)
        {
            int good = goods.IndexOf(pair.Key);
            int target = goods.IndexOf(pair.Value.For);
            if (needs[target] <= 0.0 || needs[good] > 0.0 || goods.Get(good).NeedTier != goods.Get(target).NeedTier
                || pair.Value.Counts <= 0.0 || double.IsNaN(pair.Value.Counts))
            {
                throw new InvalidDataException($"needs.json: {pair.Key} must stand in for a need of its own tier, not be a need itself, and count for more than 0.");
            }

            substituteFor[good] = target;
            substituteCounts[good] = pair.Value.Counts;
        }

        return new CraftRules(file.MaxCraftsmanShare, file.CraftsmanChangeRate, file.ReserveYears, recipes.ToArray(), needs, goods,
            substituteFor, substituteCounts);
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

        public string Requires { get; set; } = "";
    }

    private sealed class SubstituteEntry
    {
        public string For { get; set; } = "";

        public double Counts { get; set; }
    }

    private sealed class NeedsFile
    {
        public Dictionary<string, double> Needs { get; set; } = new Dictionary<string, double>();

        public Dictionary<string, SubstituteEntry> Substitutes { get; set; } = new Dictionary<string, SubstituteEntry>();
    }
}
