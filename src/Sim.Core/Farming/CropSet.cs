using Sim.Core.Climate;
using Sim.Core.Content;

namespace Sim.Core.Farming;

/// <summary>A crop group: where it grew wild and the climate it tolerates.</summary>
public sealed class Crop
{
    internal Crop(int index, CropEntry entry)
    {
        Index = index;
        Name = entry.Name;
        Colour = entry.Colour;
        HomelandLat = entry.Homeland.Lat;
        HomelandLon = entry.Homeland.Lon;
        HomelandRadiusKm = entry.Homeland.RadiusKm;
        Warmest = entry.Warmest.ToArray();
        Coldest = entry.Coldest.ToArray();
        Water = entry.Water.ToArray();
    }

    public int Index { get; }

    public string Name { get; }

    /// <summary>Display colour as "#RRGGBB".</summary>
    public string Colour { get; }

    public double HomelandLat { get; }

    public double HomelandLon { get; }

    public double HomelandRadiusKm { get; }

    public IReadOnlyList<double> Warmest { get; }

    public IReadOnlyList<double> Coldest { get; }

    public IReadOnlyList<double> Water { get; }

    /// <summary>How well the crop grows, 0 to 1, in a climate with this much water (rain plus river).</summary>
    public double Suitability(in ClimateSummary climate, double waterMm) =>
        Ramp(climate.Warmest, Warmest) * Ramp(climate.Coldest, Coldest) * Ramp(waterMm, Water);

    // 0 below the first value, rising to 1 at the second, 1 until the third, falling to 0 at the fourth.
    private static double Ramp(double value, IReadOnlyList<double> range)
    {
        double rising = (value - range[0]) / (range[1] - range[0]);
        double falling = (range[3] - value) / (range[3] - range[2]);
        return Math.Clamp(Math.Min(rising, falling), 0.0, 1.0);
    }
}

/// <summary>The crop groups, read from crops.json.</summary>
public sealed class CropSet
{
    public const int MaxCrops = 30;

    private readonly Crop[] _crops;

    private CropSet(Crop[] crops)
    {
        _crops = crops;
    }

    public IReadOnlyList<Crop> Crops => _crops;

    public int Count => _crops.Length;

    public static CropSet LoadDefault() => Parse(ContentFiles.ReadText("crops.json"));

    public static CropSet Parse(string json)
    {
        CropFile file = ContentFiles.Parse<CropFile>(json, "crops");
        if (file.Crops.Count == 0 || file.Crops.Count > MaxCrops)
        {
            throw new InvalidDataException($"The crops file must list between 1 and {MaxCrops} crops.");
        }

        var crops = new Crop[file.Crops.Count];
        for (int i = 0; i < crops.Length; i++)
        {
            CropEntry entry = file.Crops[i];
            if (string.IsNullOrWhiteSpace(entry.Name) || file.Crops.Take(i).Any(c => c.Name == entry.Name))
            {
                throw new InvalidDataException($"Crop {i + 1} has no name or repeats another's.");
            }

            foreach (List<double> range in new[] { entry.Warmest, entry.Coldest, entry.Water })
            {
                if (range.Count != 4 || range[0] >= range[1] || range[1] > range[2] || range[2] >= range[3])
                {
                    throw new InvalidDataException($"Crop '{entry.Name}' needs each range as four rising numbers.");
                }
            }

            if (entry.Warmest[0] < 10.0)
            {
                throw new InvalidDataException($"Crop '{entry.Name}' must need a warmest month of at least 10 degrees C.");
            }

            if (entry.Homeland.RadiusKm <= 0.0 || entry.Colour.Length != 7 || entry.Colour[0] != '#')
            {
                throw new InvalidDataException($"Crop '{entry.Name}' needs a homeland radius and a colour like #RRGGBB.");
            }

            crops[i] = new Crop(i, entry);
        }

        return new CropSet(crops);
    }

    public Crop Get(int index) => _crops[index];

    public int IndexOf(string name)
    {
        foreach (Crop crop in _crops)
        {
            if (crop.Name == name)
            {
                return crop.Index;
            }
        }

        throw new KeyNotFoundException($"There is no crop named '{name}'.");
    }

    internal sealed class CropFile
    {
        public List<CropEntry> Crops { get; set; } = new List<CropEntry>();
    }
}

internal sealed class CropEntry
{
    public string Name { get; set; } = "";

    public string Colour { get; set; } = "";

    public HomelandEntry Homeland { get; set; } = new HomelandEntry();

    public List<double> Warmest { get; set; } = new List<double>();

    public List<double> Coldest { get; set; } = new List<double>();

    public List<double> Water { get; set; } = new List<double>();
}

internal sealed class HomelandEntry
{
    public double Lat { get; set; }

    public double Lon { get; set; }

    public double RadiusKm { get; set; }
}

/// <summary>How farming works, read from farming.json.</summary>
public sealed class FarmingParameters
{
    public List<string> NoFarmingBiomes { get; set; } = new List<string>();

    public double MinorRiverWater { get; set; }

    public double MajorRiverWater { get; set; }

    public double WildMinSuitability { get; set; } = 0.5;

    public double RandomHomelandMinSuitability { get; set; } = 0.7;

    public double FarmDensity { get; set; } = 3.0;

    public double MajorRiverYield { get; set; } = 1.0;

    public double InventionChance { get; set; }

    public double ConversionRate { get; set; }

    public double GrowthRate { get; set; } = 0.02;

    public double MaxLoss { get; set; } = 0.2;

    public double SpreadWhenFull { get; set; } = 0.5;

    public double MinSpreadSuitability { get; set; } = 0.3;

    public double SpreadChance { get; set; }

    public double SpreadShare { get; set; } = 0.05;

    public double CropSwitchChance { get; set; }

    public double CropSwitchMargin { get; set; } = 0.2;

    public double HarvestSpread { get; set; }

    public double HarvestMin { get; set; } = 0.3;

    public double HarvestMax { get; set; } = 1.4;

    public int HarvestRegionFrequency { get; set; } = 6;

    public static FarmingParameters LoadDefault() => Parse(ContentFiles.ReadText("farming.json"));

    public static FarmingParameters Parse(string json)
    {
        FarmingParameters p = ContentFiles.Parse<FarmingParameters>(json, "farming");
        double[] shares =
        {
            p.WildMinSuitability, p.RandomHomelandMinSuitability, p.InventionChance, p.ConversionRate, p.GrowthRate,
            p.MaxLoss, p.SpreadWhenFull, p.MinSpreadSuitability, p.SpreadChance, p.SpreadShare, p.CropSwitchChance,
        };
        if (shares.Any(v => v < 0.0 || v > 1.0 || double.IsNaN(v)))
        {
            throw new InvalidDataException("Farming chances, rates and shares must be between 0 and 1.");
        }

        if (p.FarmDensity <= 0.0 || p.MajorRiverYield <= 0.0 || p.MinorRiverWater < 0.0 || p.MajorRiverWater < 0.0
            || p.HarvestSpread < 0.0 || p.HarvestMin <= 0.0 || p.HarvestMax < 1.0
            || p.HarvestRegionFrequency < 1 || p.HarvestRegionFrequency > 30)
        {
            throw new InvalidDataException("A farming number is out of range.");
        }

        return p;
    }
}
