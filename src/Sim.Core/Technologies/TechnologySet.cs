using Sim.Core.Content;
using Sim.Core.Map;
using Sim.Core.People;

namespace Sim.Core.Technologies;

/// <summary>A technology: who can use it, what it needs, and how readily it is found and spreads.</summary>
public sealed class Technology
{
    internal Technology(int index, TechnologyEntry entry, bool[] occupations, int[] requires, TerrainClass[] terrain)
    {
        Index = index;
        Name = entry.Name;
        Colour = entry.Colour;
        CanUse = occupations;
        Requires = requires;
        River = entry.River;
        MaxRainMm = entry.MaxRainMm;
        Coast = entry.Coast;
        Terrain = terrain;
        Animals = entry.Animals.ToArray();
        Crops = entry.Crops.ToArray();
        NearGoods = entry.NearGoods.ToArray();
        NearWild = entry.NearWild.ToArray();
        SettledYears = entry.SettledYears;
        Threatened = entry.Threatened;
        Chance = entry.Chance;
        Spread = entry.Spread;
        History = entry.History;
        Effects = new Dictionary<string, double>(entry.Effects, StringComparer.Ordinal);
    }

    public int Index { get; }

    public string Name { get; }

    public string Slug => new string(Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>Display colour as "#RRGGBB".</summary>
    public string Colour { get; }

    /// <summary>By occupation index: whether people of that occupation can discover and use it.</summary>
    public IReadOnlyList<bool> CanUse { get; }

    /// <summary>Technologies (by index) a group must use first.</summary>
    public IReadOnlyList<int> Requires { get; }

    public bool River { get; }

    /// <summary>The most rain a year (mm) where it can be used; 0 for no limit.</summary>
    public double MaxRainMm { get; }

    public bool Coast { get; }

    /// <summary>Terrains where it can be used; empty for any.</summary>
    public IReadOnlyList<TerrainClass> Terrain { get; }

    /// <summary>Animal names, at least one of which must be kept in the cell; empty for none needed.</summary>
    public IReadOnlyList<string> Animals { get; }

    /// <summary>Crop names, one of which must be farmed in the cell; empty for none needed.</summary>
    public IReadOnlyList<string> Crops { get; }

    /// <summary>
    /// Goods (by name), one of which must be within reach to discover, learn, use and keep it:
    /// gatherable in the cell, in its store, or held or gathered by a settlement a few trade links
    /// away (technology.json "supplyLinks"). Empty for none.
    /// </summary>
    public IReadOnlyList<string> NearGoods { get; }

    /// <summary>
    /// Animals (by name), one of which must be near: to discover it, living wild in the cell; to
    /// learn, use and keep it, wild or kept there. Empty for none.
    /// </summary>
    public IReadOnlyList<string> NearWild { get; }

    /// <summary>For discovery only: farming must have stood in the cell or a neighbour at least this many years (0: no need).</summary>
    public double SettledYears { get; }

    /// <summary>For discovery only: the cell must belong to a polity at war or bordering another (polities.threatened).</summary>
    public bool Threatened { get; }

    public double Chance { get; }

    public double Spread { get; }

    public string History { get; }

    /// <summary>What it changes where it is used: effect name (see <see cref="TechnologySet.EffectNames"/>) and size.</summary>
    public IReadOnlyDictionary<string, double> Effects { get; }
}

/// <summary>The technologies, read from technologies.json.</summary>
public sealed class TechnologySet
{
    public const int MaxTechnologies = 30;

    // Effect names, and the systems that apply them.
    /// <summary>Farming: multiplies what a farming cell's land feeds.</summary>
    public const string FarmYield = "farmYield";

    /// <summary>Farming: the share (0 to 1) of a biome's farming penalty (farming.json "biomeFactors") lifted.</summary>
    public const string BiomePenaltyLift = "biomePenaltyLift";

    /// <summary>Goods: multiplies how much grain spoils in a settlement's store each year.</summary>
    public const string GrainSpoilage = "grainSpoilage";

    /// <summary>Herding: multiplies how many herders a cell's pasture feeds.</summary>
    public const string HerdDensity = "herdDensity";

    /// <summary>Herding: multiplies the wool herders and farmers bring to a store.</summary>
    public const string Wool = "wool";

    /// <summary>Herding: multiplies herders' yearly chance of moving onto new pasture.</summary>
    public const string HerderSpread = "herderSpread";

    /// <summary>Trade: multiplies the cost of carrying goods over land on links where both settlements use it.</summary>
    public const string LandCarrying = "landCarrying";

    /// <summary>Seafaring and trade: how many sea cells in a row people's boats can cross (the largest known counts).</summary>
    public const string SeaRange = "seaRange";

    /// <summary>Trade: the cost of carrying goods across one sea cell, against 1 for a cell of lowland (the smallest known counts).</summary>
    public const string SeaCost = "seaCost";

    /// <summary>Multiplies the strength of a polity defending a cell where it is used (walls).</summary>
    public const string Defence = "defence";

    /// <summary>Multiplies the strength of a polity attacking over open lowland, where its capital uses it (chariots).</summary>
    public const string Attack = "attack";

    public static readonly IReadOnlyList<string> EffectNames = new[] { FarmYield, BiomePenaltyLift, GrainSpoilage, HerdDensity, Wool, HerderSpread, LandCarrying, SeaRange, SeaCost, Defence, Attack };

    private readonly Technology[] _technologies;

    private TechnologySet(Technology[] technologies)
    {
        _technologies = technologies;
    }

    public IReadOnlyList<Technology> Technologies => _technologies;

    public int Count => _technologies.Length;

    public Technology Get(int index) => _technologies[index];

    public static TechnologySet LoadDefault(OccupationSet occupations) => Parse(ContentFiles.ReadText("technologies.json"), occupations);

    public static TechnologySet Parse(string json, OccupationSet occupations)
    {
        TechnologyFile file = ContentFiles.Parse<TechnologyFile>(json, "technologies");
        if (file.Technologies.Count == 0 || file.Technologies.Count > MaxTechnologies)
        {
            throw new InvalidDataException($"The technologies file must list between 1 and {MaxTechnologies} technologies.");
        }

        var names = file.Technologies.Select(t => t.Name).ToList();
        var technologies = new Technology[names.Count];
        for (int i = 0; i < technologies.Length; i++)
        {
            TechnologyEntry entry = file.Technologies[i];
            if (string.IsNullOrWhiteSpace(entry.Name) || names.Take(i).Contains(entry.Name))
            {
                throw new InvalidDataException($"Technology {i + 1} has no name or repeats another's.");
            }

            if (entry.Colour.Length != 7 || entry.Colour[0] != '#' || entry.Occupations.Count == 0
                || entry.Chance < 0.0 || entry.Chance > 1.0 || entry.Spread < 0.0 || entry.Spread > 1.0 || entry.MaxRainMm < 0.0)
            {
                throw new InvalidDataException($"Technology '{entry.Name}' needs a colour like #RRGGBB, occupations, and chance and spread between 0 and 1.");
            }

            if (entry.SettledYears < 0.0 || double.IsNaN(entry.SettledYears))
            {
                throw new InvalidDataException($"Technology '{entry.Name}': settledYears must not be negative.");
            }

            foreach (KeyValuePair<string, double> effect in entry.Effects)
            {
                if (!EffectNames.Contains(effect.Key) || effect.Value < 0.0 || double.IsNaN(effect.Value) || double.IsInfinity(effect.Value)
                    || (effect.Key == BiomePenaltyLift && effect.Value > 1.0))
                {
                    throw new InvalidDataException($"Technology '{entry.Name}' has effect '{effect.Key}' = {effect.Value}; effects are {string.Join(", ", EffectNames)}, at least 0 ({BiomePenaltyLift} at most 1).");
                }
            }

            var canUse = new bool[occupations.Count];
            foreach (string occupation in entry.Occupations)
            {
                canUse[occupations.IndexOf(occupation)] = true;
            }

            var requires = new int[entry.Requires.Count];
            for (int r = 0; r < requires.Length; r++)
            {
                int index = names.IndexOf(entry.Requires[r]);
                if (index < 0 || index >= i)
                {
                    throw new InvalidDataException($"Technology '{entry.Name}' requires '{entry.Requires[r]}', which must be listed before it.");
                }

                requires[r] = index;
            }

            var terrain = new TerrainClass[entry.Terrain.Count];
            for (int t = 0; t < terrain.Length; t++)
            {
                if (!Enum.TryParse(entry.Terrain[t], ignoreCase: true, out terrain[t]) || terrain[t] is TerrainClass.Ocean or TerrainClass.Coast)
                {
                    throw new InvalidDataException($"Technology '{entry.Name}' has terrain '{entry.Terrain[t]}'; use lowland, hills, mountains or lake.");
                }
            }

            technologies[i] = new Technology(i, entry, canUse, requires, terrain);
        }

        return new TechnologySet(technologies);
    }

    public int IndexOf(string name)
    {
        foreach (Technology technology in _technologies)
        {
            if (technology.Name == name)
            {
                return technology.Index;
            }
        }

        throw new KeyNotFoundException($"There is no technology named '{name}'.");
    }

    internal sealed class TechnologyFile
    {
        public List<TechnologyEntry> Technologies { get; set; } = new List<TechnologyEntry>();
    }
}

internal sealed class TechnologyEntry
{
    public string Name { get; set; } = "";

    public string Colour { get; set; } = "";

    public List<string> Occupations { get; set; } = new List<string>();

    public List<string> Requires { get; set; } = new List<string>();

    public bool River { get; set; }

    public double MaxRainMm { get; set; }

    public bool Coast { get; set; }

    public List<string> Terrain { get; set; } = new List<string>();

    public List<string> Animals { get; set; } = new List<string>();

    public List<string> Crops { get; set; } = new List<string>();

    public List<string> NearGoods { get; set; } = new List<string>();

    public List<string> NearWild { get; set; } = new List<string>();

    public double SettledYears { get; set; }

    public bool Threatened { get; set; }

    public double Chance { get; set; }

    public double Spread { get; set; }

    public string History { get; set; } = "";

    public Dictionary<string, double> Effects { get; set; } = new Dictionary<string, double>();
}

/// <summary>How technology spreads and is lost, read from technology.json.</summary>
public sealed class TechnologyParameters
{
    public double InUse { get; set; } = 0.5;

    public double DiscoveryShare { get; set; } = 0.1;

    public double DiscoveryPeople { get; set; } = 20000.0;

    public double Adoption { get; set; } = 0.05;

    public double WithinCell { get; set; } = 0.2;

    public double TradeSpread { get; set; } = 0.02;

    public double BusyTrade { get; set; } = 50.0;

    /// <summary>How fast knowledge passes between a polity's settlements and its capital, as a share of tradeSpread.</summary>
    public double RuleSpread { get; set; } = 0.5;

    public double KeepPeople { get; set; } = 3000.0;

    public double LossRate { get; set; } = 0.03;

    /// <summary>The share of what a group knows of a technology that it forgets each year where its cell does not meet the technology's conditions.</summary>
    public double UnusedLoss { get; set; }

    /// <summary>How many trade links away goods count as near, for learning, using and keeping a technology that needs them.</summary>
    public int SupplyLinks { get; set; } = 1;

    public double IndependentKm { get; set; } = 1000.0;

    public static TechnologyParameters LoadDefault() => Parse(ContentFiles.ReadText("technology.json"));

    public static TechnologyParameters Parse(string json)
    {
        TechnologyParameters p = ContentFiles.Parse<TechnologyParameters>(json, "technology");
        double[] shares = { p.InUse, p.DiscoveryShare, p.Adoption, p.WithinCell, p.TradeSpread, p.LossRate, p.UnusedLoss, p.RuleSpread };
        if (shares.Any(v => v < 0.0 || v > 1.0 || double.IsNaN(v)) || p.InUse <= 0.0
            || p.SupplyLinks < 0 || p.DiscoveryPeople <= 0.0 || p.BusyTrade <= 0.0 || p.KeepPeople < 0.0 || p.IndependentKm < 0.0)
        {
            throw new InvalidDataException("A technology number is out of range.");
        }

        return p;
    }
}
