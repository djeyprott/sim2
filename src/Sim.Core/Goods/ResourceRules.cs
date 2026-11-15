using Sim.Core.Content;

namespace Sim.Core.Goods;

/// <summary>A listed place a good comes from.</summary>
public sealed class ResourceSource
{
    public string Name { get; set; } = "";

    public double Lat { get; set; }

    public double Lon { get; set; }
}

/// <summary>Where one good can be gathered, and how much (see resources.json).</summary>
public sealed class ResourceRule
{
    public string Good { get; set; } = "";

    public double PerPerson { get; set; }

    public Dictionary<string, double> Biomes { get; set; } = new Dictionary<string, double>();

    public Dictionary<string, double> Terrain { get; set; } = new Dictionary<string, double>();

    public double River { get; set; }

    public double Coast { get; set; }

    public double DryCoast { get; set; }

    public double DryLakeShore { get; set; }

    public double Farmland { get; set; }

    public double SourceRadiusKm { get; set; } = 60.0;

    public List<ResourceSource> Sources { get; set; } = new List<ResourceSource>();
}

/// <summary>The gathering rules, read from resources.json.</summary>
public sealed class ResourceRules
{
    public List<string> Gatherers { get; set; } = new List<string>();

    public double DryRainMm { get; set; } = 600.0;

    public List<ResourceRule> Goods { get; set; } = new List<ResourceRule>();

    public static ResourceRules LoadDefault() => Parse(ContentFiles.ReadText("resources.json"));

    public static ResourceRules Parse(string json)
    {
        ResourceRules rules = ContentFiles.Parse<ResourceRules>(json, "resources");
        if (rules.Gatherers.Count == 0)
        {
            throw new InvalidDataException("resources.json must name at least one gatherer occupation.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ResourceRule rule in rules.Goods)
        {
            if (!seen.Add(rule.Good))
            {
                throw new InvalidDataException($"resources.json lists '{rule.Good}' twice.");
            }

            IEnumerable<double> factors = rule.Biomes.Values.Concat(rule.Terrain.Values)
                .Concat(new[] { rule.River, rule.Coast, rule.DryCoast, rule.DryLakeShore, rule.Farmland });
            if (factors.Any(f => f < 0.0 || f > 1.0 || double.IsNaN(f)))
            {
                throw new InvalidDataException($"{rule.Good}: richness factors must be between 0 and 1.");
            }

            if (rule.PerPerson < 0.0 || rule.SourceRadiusKm < 0.0)
            {
                throw new InvalidDataException($"{rule.Good}: perPerson and sourceRadiusKm must not be negative.");
            }

            foreach (string terrain in rule.Terrain.Keys)
            {
                if (terrain != "lowland" && terrain != "hills" && terrain != "mountains")
                {
                    throw new InvalidDataException($"{rule.Good}: terrain must be lowland, hills or mountains, not '{terrain}'.");
                }
            }
        }

        return rules;
    }
}
