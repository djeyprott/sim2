using Sim.Core.Content;

namespace Sim.Core.Goods;

/// <summary>Which need a good meets.</summary>
public enum NeedTier
{
    None,
    Life,
    Everyday,
    Luxury,
}

/// <summary>One kind of good, read from goods.json.</summary>
public sealed class Good
{
    public int Index { get; internal set; }

    public string Name { get; set; } = "";

    public string Unit { get; set; } = "";

    /// <summary>Share of a stock lost per year in store.</summary>
    public double Spoilage { get; set; }

    /// <summary>Weight of one unit to carry, relative to a person-year of grain.</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>Base price in food (person-years of grain).</summary>
    public double Value { get; set; } = 1.0;

    public string Tier { get; set; } = "none";

    public bool Food { get; set; }

    public string Colour { get; set; } = "#ffffff";

    public NeedTier NeedTier { get; internal set; }

    /// <summary>The share of a stock that spoils in one month.</summary>
    public double MonthlySpoilage { get; internal set; }

    /// <summary>The good's name as used in state field names: lower case, letters and digits, others as '_'.</summary>
    public string Slug => new string(Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}

/// <summary>The list of goods, in file order. A good's index is its position in the file.</summary>
public sealed class GoodSet
{
    public const int MaxGoods = 64;

    public const string Grain = "Grain";
    public const string WildFood = "Wild food";

    private readonly Good[] _goods;

    private GoodSet(Good[] goods)
    {
        _goods = goods;
    }

    public IReadOnlyList<Good> Goods => _goods;

    public int Count => _goods.Length;

    public Good Get(int index) => _goods[index];

    public static GoodSet LoadDefault() => Parse(ContentFiles.ReadText("goods.json"));

    public static GoodSet Parse(string json)
    {
        GoodsFile file = ContentFiles.Parse<GoodsFile>(json, "goods");
        Good[] goods = file.Goods.ToArray();
        if (goods.Length == 0 || goods.Length > MaxGoods)
        {
            throw new InvalidDataException($"goods.json must list between 1 and {MaxGoods} goods.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < goods.Length; i++)
        {
            Good good = goods[i];
            good.Index = i;
            if (string.IsNullOrWhiteSpace(good.Name) || !names.Add(good.Name) || !slugs.Add(good.Slug))
            {
                throw new InvalidDataException($"Good {i} has a missing or repeated name.");
            }

            if (good.Spoilage < 0.0 || good.Spoilage >= 1.0 || double.IsNaN(good.Spoilage) || good.Weight <= 0.0 || good.Value <= 0.0)
            {
                throw new InvalidDataException($"{good.Name}: spoilage must be in [0, 1), and weight and value above 0.");
            }

            good.NeedTier = good.Tier.ToLowerInvariant() switch
            {
                "none" => NeedTier.None,
                "life" => NeedTier.Life,
                "everyday" => NeedTier.Everyday,
                "luxury" => NeedTier.Luxury,
                _ => throw new InvalidDataException($"{good.Name}: tier must be none, life, everyday or luxury."),
            };
            good.MonthlySpoilage = 1.0 - Math.Pow(1.0 - good.Spoilage, 1.0 / 12.0);
        }

        foreach (string required in new[] { Grain, WildFood })
        {
            if (!names.Contains(required))
            {
                throw new InvalidDataException($"goods.json must list '{required}'.");
            }
        }

        return new GoodSet(goods);
    }

    public int IndexOf(string name)
    {
        for (int i = 0; i < _goods.Length; i++)
        {
            if (_goods[i].Name == name)
            {
                return i;
            }
        }

        throw new KeyNotFoundException($"Unknown good '{name}'.");
    }

    private sealed class GoodsFile
    {
        public List<Good> Goods { get; set; } = new List<Good>();
    }
}
