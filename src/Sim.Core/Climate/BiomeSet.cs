using Sim.Core.Content;

namespace Sim.Core.Climate;

/// <summary>A kind of landscape, assigned to each land cell from its current climate.</summary>
public sealed class Biome
{
    internal Biome(int index, string name, string colour)
    {
        Index = index;
        Name = name;
        Colour = colour;
    }

    public int Index { get; }

    public string Name { get; }

    /// <summary>Display colour as "#RRGGBB".</summary>
    public string Colour { get; }
}

/// <summary>The measures of a year's climate that biome rules are written against.</summary>
public readonly struct ClimateSummary
{
    public const int MeasureCount = 8;

    public ClimateSummary(double mean, double warmest, double coldest, double rain, double driest, double summerShare)
    {
        Mean = mean;
        Warmest = warmest;
        Coldest = coldest;
        Rain = rain;
        Driest = driest;
        SummerShare = summerShare;

        // Koppen dryness threshold (Peel et al. 2007), in cm of rain a year.
        double threshold = 2.0 * mean + (summerShare >= 0.7 ? 28.0 : summerShare <= 0.3 ? 0.0 : 14.0);
        Dryness = threshold > 0.0 ? rain / (10.0 * threshold) : 1e9;
        MonsoonIndex = driest + rain / 25.0 - 100.0;
    }

    public double Mean { get; }

    public double Warmest { get; }

    public double Coldest { get; }

    public double Rain { get; }

    public double Driest { get; }

    public double SummerShare { get; }

    public double Dryness { get; }

    public double MonsoonIndex { get; }

    /// <summary>Summarises twelve monthly values, January first.</summary>
    public static ClimateSummary FromMonths(ReadOnlySpan<double> temperatureC, ReadOnlySpan<double> rainfallMm, bool northernHemisphere)
    {
        if (temperatureC.Length != 12 || rainfallMm.Length != 12)
        {
            throw new ArgumentException("Twelve months are needed.");
        }

        double sum = 0.0;
        double warmest = double.NegativeInfinity;
        double coldest = double.PositiveInfinity;
        double rain = 0.0;
        double driest = double.PositiveInfinity;
        double summer = 0.0;
        for (int month = 0; month < 12; month++)
        {
            double t = temperatureC[month];
            double p = rainfallMm[month];
            sum += t;
            warmest = Math.Max(warmest, t);
            coldest = Math.Min(coldest, t);
            rain += p;
            driest = Math.Min(driest, p);

            // Months 3 to 8 are April to September.
            bool aprilToSeptember = month >= 3 && month <= 8;
            if (aprilToSeptember == northernHemisphere)
            {
                summer += p;
            }
        }

        return new ClimateSummary(sum / 12.0, warmest, coldest, rain, driest, rain > 0.0 ? summer / rain : 0.5);
    }
}

/// <summary>The list of biomes and the rules that assign them, read from biomes.json.</summary>
public sealed class BiomeSet
{
    private readonly Biome[] _biomes;
    private readonly int[] _ruleBiome;
    private readonly double[] _ruleMin;
    private readonly double[] _ruleMax;

    private BiomeSet(Biome[] biomes, int[] ruleBiome, double[] ruleMin, double[] ruleMax)
    {
        _biomes = biomes;
        _ruleBiome = ruleBiome;
        _ruleMin = ruleMin;
        _ruleMax = ruleMax;
    }

    public IReadOnlyList<Biome> Biomes => _biomes;

    public int Count => _biomes.Length;

    public static BiomeSet LoadDefault() => Parse(ContentFiles.ReadText("biomes.json"));

    public static BiomeSet Parse(string json)
    {
        BiomeFile file = ContentFiles.Parse<BiomeFile>(json, "biomes");
        if (file.Biomes.Count == 0 || file.Biomes.Count > 250)
        {
            throw new InvalidDataException("The biomes file must list between 1 and 250 biomes.");
        }

        var biomes = new Biome[file.Biomes.Count];
        for (int i = 0; i < biomes.Length; i++)
        {
            BiomeEntry entry = file.Biomes[i];
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                throw new InvalidDataException($"Biome {i + 1} has no name.");
            }

            for (int j = 0; j < i; j++)
            {
                if (biomes[j].Name == entry.Name)
                {
                    throw new InvalidDataException($"Biome '{entry.Name}' is listed twice.");
                }
            }

            if (entry.Colour.Length != 7 || entry.Colour[0] != '#'
                || !int.TryParse(entry.Colour.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _))
            {
                throw new InvalidDataException($"Biome '{entry.Name}' has colour '{entry.Colour}'; use the form #RRGGBB.");
            }

            biomes[i] = new Biome(i, entry.Name, entry.Colour);
        }

        if (file.Rules.Count == 0)
        {
            throw new InvalidDataException("The biomes file has no rules.");
        }

        int n = ClimateSummary.MeasureCount;
        var ruleBiome = new int[file.Rules.Count];
        var ruleMin = new double[file.Rules.Count * n];
        var ruleMax = new double[file.Rules.Count * n];
        for (int r = 0; r < file.Rules.Count; r++)
        {
            RuleEntry rule = file.Rules[r];
            int biome = Array.FindIndex(biomes, b => b.Name == rule.Biome);
            if (biome < 0)
            {
                throw new InvalidDataException($"Rule {r + 1} names biome '{rule.Biome}', which is not in the list.");
            }

            ruleBiome[r] = biome;
            double?[] mins = { rule.MinMean, rule.MinWarmest, rule.MinColdest, rule.MinRain, rule.MinDriest, rule.MinSummerShare, rule.MinDryness, rule.MinMonsoonIndex };
            double?[] maxs = { rule.MaxMean, rule.MaxWarmest, rule.MaxColdest, rule.MaxRain, rule.MaxDriest, rule.MaxSummerShare, rule.MaxDryness, rule.MaxMonsoonIndex };
            bool any = false;
            for (int m = 0; m < n; m++)
            {
                ruleMin[r * n + m] = mins[m] ?? double.NegativeInfinity;
                ruleMax[r * n + m] = maxs[m] ?? double.PositiveInfinity;
                any |= mins[m].HasValue || maxs[m].HasValue;
            }

            if (r == file.Rules.Count - 1 && any)
            {
                throw new InvalidDataException("The last biome rule must have no conditions, so every cell gets a biome.");
            }
        }

        return new BiomeSet(biomes, ruleBiome, ruleMin, ruleMax);
    }

    public Biome Get(int index) => _biomes[index];

    public int IndexOf(string name)
    {
        for (int i = 0; i < _biomes.Length; i++)
        {
            if (_biomes[i].Name == name)
            {
                return i;
            }
        }

        throw new KeyNotFoundException($"There is no biome named '{name}'.");
    }

    /// <summary>The index of the biome the first matching rule assigns.</summary>
    public int Classify(in ClimateSummary climate)
    {
        Span<double> values = stackalloc double[ClimateSummary.MeasureCount];
        values[0] = climate.Mean;
        values[1] = climate.Warmest;
        values[2] = climate.Coldest;
        values[3] = climate.Rain;
        values[4] = climate.Driest;
        values[5] = climate.SummerShare;
        values[6] = climate.Dryness;
        values[7] = climate.MonsoonIndex;

        int n = ClimateSummary.MeasureCount;
        for (int r = 0; r < _ruleBiome.Length; r++)
        {
            bool match = true;
            for (int m = 0; m < n && match; m++)
            {
                double v = values[m];
                match = v >= _ruleMin[r * n + m] && v < _ruleMax[r * n + m];
            }

            if (match)
            {
                return _ruleBiome[r];
            }
        }

        return _ruleBiome[_ruleBiome.Length - 1];
    }

    private sealed class BiomeFile
    {
        public List<BiomeEntry> Biomes { get; set; } = new List<BiomeEntry>();

        public List<RuleEntry> Rules { get; set; } = new List<RuleEntry>();
    }

    private sealed class BiomeEntry
    {
        public string Name { get; set; } = "";

        public string Colour { get; set; } = "";
    }

    private sealed class RuleEntry
    {
        public string Biome { get; set; } = "";

        public double? MinMean { get; set; }

        public double? MaxMean { get; set; }

        public double? MinWarmest { get; set; }

        public double? MaxWarmest { get; set; }

        public double? MinColdest { get; set; }

        public double? MaxColdest { get; set; }

        public double? MinRain { get; set; }

        public double? MaxRain { get; set; }

        public double? MinDriest { get; set; }

        public double? MaxDriest { get; set; }

        public double? MinSummerShare { get; set; }

        public double? MaxSummerShare { get; set; }

        public double? MinDryness { get; set; }

        public double? MaxDryness { get; set; }

        public double? MinMonsoonIndex { get; set; }

        public double? MaxMonsoonIndex { get; set; }
    }
}
