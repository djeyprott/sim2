using Sim.Core.Climate;
using Sim.Core.Content;

namespace Sim.Core.Herding;

/// <summary>A herd animal: where it lived wild, the climate it tolerates and what it gives.</summary>
public sealed class Animal
{
    internal Animal(int index, AnimalEntry entry, int firstHomeland)
    {
        Index = index;
        Name = entry.Name;
        Colour = entry.Colour;
        Herded = entry.Herded;
        Wool = entry.Wool;
        FirstHomeland = firstHomeland;
        HomelandCount = entry.Homelands.Count;
        Warmest = entry.Warmest.ToArray();
        Coldest = entry.Coldest.ToArray();
        Water = entry.Water.ToArray();
    }

    public int Index { get; }

    public string Name { get; }

    /// <summary>Display colour as "#RRGGBB".</summary>
    public string Colour { get; }

    /// <summary>Kept by herders on open pasture; otherwise only by farmers.</summary>
    public bool Herded { get; }

    public bool Wool { get; }

    /// <summary>Index of the animal's first homeland in <see cref="LivestockSet.Homelands"/>.</summary>
    public int FirstHomeland { get; }

    public int HomelandCount { get; }

    public IReadOnlyList<double> Warmest { get; }

    public IReadOnlyList<double> Coldest { get; }

    public IReadOnlyList<double> Water { get; }

    /// <summary>How well the animal does, 0 to 1, in a climate with this much water (rain plus river).</summary>
    public double Suitability(in ClimateSummary climate, double waterMm) =>
        Ramp(climate.Warmest, Warmest) * Ramp(climate.Coldest, Coldest) * Ramp(waterMm, Water);

    private static double Ramp(double value, IReadOnlyList<double> range)
    {
        double rising = (value - range[0]) / (range[1] - range[0]);
        double falling = (range[3] - value) / (range[3] - range[2]);
        return Math.Clamp(Math.Min(rising, falling), 0.0, 1.0);
    }
}

/// <summary>One of an animal's homelands: a circle on the real Earth.</summary>
public sealed record AnimalHomeland(int Index, int Animal, double Lat, double Lon, double RadiusKm);

/// <summary>The herd animals, read from livestock.json.</summary>
public sealed class LivestockSet
{
    /// <summary>Animals and homelands are kept as bits in an int per cell.</summary>
    public const int MaxBits = 30;

    private readonly Animal[] _animals;
    private readonly AnimalHomeland[] _homelands;

    private LivestockSet(Animal[] animals, AnimalHomeland[] homelands)
    {
        _animals = animals;
        _homelands = homelands;
    }

    public IReadOnlyList<Animal> Animals => _animals;

    public IReadOnlyList<AnimalHomeland> Homelands => _homelands;

    public int Count => _animals.Length;

    public Animal Get(int index) => _animals[index];

    public static LivestockSet LoadDefault() => Parse(ContentFiles.ReadText("livestock.json"));

    public static LivestockSet Parse(string json)
    {
        LivestockFile file = ContentFiles.Parse<LivestockFile>(json, "livestock");
        if (file.Animals.Count == 0 || file.Animals.Count > MaxBits)
        {
            throw new InvalidDataException($"The livestock file must list between 1 and {MaxBits} animals.");
        }

        var animals = new Animal[file.Animals.Count];
        var homelands = new List<AnimalHomeland>();
        for (int i = 0; i < animals.Length; i++)
        {
            AnimalEntry entry = file.Animals[i];
            if (string.IsNullOrWhiteSpace(entry.Name) || file.Animals.Take(i).Any(a => a.Name == entry.Name))
            {
                throw new InvalidDataException($"Animal {i + 1} has no name or repeats another's.");
            }

            foreach (List<double> range in new[] { entry.Warmest, entry.Coldest, entry.Water })
            {
                if (range.Count != 4 || range[0] >= range[1] || range[1] > range[2] || range[2] >= range[3])
                {
                    throw new InvalidDataException($"Animal '{entry.Name}' needs each range as four rising numbers.");
                }
            }

            if (entry.Homelands.Count == 0 || entry.Homelands.Any(h => h.RadiusKm <= 0.0) || entry.Colour.Length != 7 || entry.Colour[0] != '#')
            {
                throw new InvalidDataException($"Animal '{entry.Name}' needs at least one homeland with a radius, and a colour like #RRGGBB.");
            }

            animals[i] = new Animal(i, entry, homelands.Count);
            foreach (HomelandCircle circle in entry.Homelands)
            {
                homelands.Add(new AnimalHomeland(homelands.Count, i, circle.Lat, circle.Lon, circle.RadiusKm));
            }
        }

        if (homelands.Count > MaxBits)
        {
            throw new InvalidDataException($"The livestock file may list at most {MaxBits} homelands in all.");
        }

        return new LivestockSet(animals, homelands.ToArray());
    }

    public int IndexOf(string name)
    {
        foreach (Animal animal in _animals)
        {
            if (animal.Name == name)
            {
                return animal.Index;
            }
        }

        throw new KeyNotFoundException($"There is no animal named '{name}'.");
    }

    internal sealed class LivestockFile
    {
        public List<AnimalEntry> Animals { get; set; } = new List<AnimalEntry>();
    }
}

internal sealed class AnimalEntry
{
    public string Name { get; set; } = "";

    public string Colour { get; set; } = "";

    public bool Herded { get; set; } = true;

    public bool Wool { get; set; }

    public List<HomelandCircle> Homelands { get; set; } = new List<HomelandCircle>();

    public List<double> Warmest { get; set; } = new List<double>();

    public List<double> Coldest { get; set; } = new List<double>();

    public List<double> Water { get; set; } = new List<double>();
}

internal sealed class HomelandCircle
{
    public double Lat { get; set; }

    public double Lon { get; set; }

    public double RadiusKm { get; set; }
}

/// <summary>How herding works, read from herding.json.</summary>
public sealed class HerdingParameters
{
    public Dictionary<string, double> Pasture { get; set; } = new Dictionary<string, double>();

    public double MinPasture { get; set; } = 0.5;

    public double HerdDensity { get; set; } = 1.0;

    public double InventionChance { get; set; }

    public double ForagerInventionFactor { get; set; } = 1.0;

    public double WildMinSuitability { get; set; } = 0.5;

    public double RandomHomelandMinSuitability { get; set; } = 0.7;

    public double ConversionRate { get; set; }

    public double FirstHerderShare { get; set; } = 0.1;

    public double GrowthRate { get; set; } = 0.03;

    public double DeathShare { get; set; } = 0.2;

    public double LeaveShare { get; set; } = 0.3;

    public double SpreadWhenFull { get; set; } = 0.4;

    public double MinSpreadSuitability { get; set; } = 0.3;

    public double SpreadChance { get; set; }

    public double SpreadShare { get; set; } = 0.1;

    public double WoolPerHerder { get; set; }

    public double HidesPerHerder { get; set; }

    public double WoolPerFarmer { get; set; }

    public double HidesPerFarmer { get; set; }

    public static HerdingParameters LoadDefault() => Parse(ContentFiles.ReadText("herding.json"));

    public static HerdingParameters Parse(string json)
    {
        HerdingParameters p = ContentFiles.Parse<HerdingParameters>(json, "herding");
        double[] shares =
        {
            p.MinPasture, p.InventionChance, p.ForagerInventionFactor, p.WildMinSuitability, p.RandomHomelandMinSuitability, p.ConversionRate, p.FirstHerderShare,
            p.GrowthRate, p.DeathShare, p.LeaveShare, p.SpreadWhenFull, p.MinSpreadSuitability, p.SpreadChance, p.SpreadShare,
        };
        if (shares.Any(v => v < 0.0 || v > 1.0 || double.IsNaN(v)) || p.Pasture.Values.Any(v => v < 0.0 || v > 1.0 || double.IsNaN(v)))
        {
            throw new InvalidDataException("Herding chances, rates, shares and pasture must be between 0 and 1.");
        }

        if (p.DeathShare + p.LeaveShare > 1.0)
        {
            throw new InvalidDataException("deathShare and leaveShare together must not exceed 1.");
        }

        if (p.HerdDensity <= 0.0 || new[] { p.WoolPerHerder, p.HidesPerHerder, p.WoolPerFarmer, p.HidesPerFarmer }.Any(v => v < 0.0 || double.IsNaN(v)))
        {
            throw new InvalidDataException("A herding number is out of range.");
        }

        return p;
    }
}
