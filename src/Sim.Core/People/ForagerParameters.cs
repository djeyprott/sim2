using Sim.Core.Content;

namespace Sim.Core.People;

/// <summary>A named point on the map, used to pick out a landmass.</summary>
public sealed class Place
{
    public string Name { get; set; } = "";

    public double Lat { get; set; }

    public double Lon { get; set; }
}

/// <summary>How foragers live and move, read from foragers.json.</summary>
public sealed class ForagerParameters
{
    /// <summary>People per square km each biome can feed, by biome name.</summary>
    public Dictionary<string, double> Density { get; set; } = new Dictionary<string, double>();

    public double CoastFactor { get; set; } = 1.0;

    public double LakeShoreFactor { get; set; } = 1.0;

    /// <summary>Extra people per square km on a minor river.</summary>
    public double MinorRiverBonus { get; set; }

    /// <summary>Extra people per square km on a major river.</summary>
    public double MajorRiverBonus { get; set; }

    public double GrowthRate { get; set; } = 0.03;

    public double MigrationRate { get; set; } = 0.1;

    public double LossWhereNoFood { get; set; } = 0.2;

    public int MaxSeaCellsCrossed { get; set; } = 2;

    public List<Place> Reached { get; set; } = new List<Place>();

    public List<Place> Unreached { get; set; } = new List<Place>();

    public static ForagerParameters LoadDefault() => Parse(ContentFiles.ReadText("foragers.json"));

    public static ForagerParameters Parse(string json)
    {
        ForagerParameters parameters = ContentFiles.Parse<ForagerParameters>(json, "foragers");
        parameters.Validate();
        return parameters;
    }

    private void Validate()
    {
        if (Density.Values.Any(d => d < 0.0 || double.IsNaN(d)))
        {
            throw new InvalidDataException("Forager densities cannot be negative.");
        }

        if (CoastFactor <= 0.0 || LakeShoreFactor <= 0.0)
        {
            throw new InvalidDataException("Coast and lake shore factors must be positive.");
        }

        if (MinorRiverBonus < 0.0 || MajorRiverBonus < 0.0)
        {
            throw new InvalidDataException("River bonuses cannot be negative.");
        }

        if (GrowthRate <= 0.0 || GrowthRate > 1.0 || MigrationRate < 0.0 || MigrationRate > 1.0
            || LossWhereNoFood < 0.0 || LossWhereNoFood > 1.0)
        {
            throw new InvalidDataException("Growth, migration and loss rates must be between 0 and 1.");
        }

        if (MaxSeaCellsCrossed < 0 || MaxSeaCellsCrossed > 20)
        {
            throw new InvalidDataException("maxSeaCellsCrossed must be between 0 and 20.");
        }

        if (Reached.Count == 0)
        {
            throw new InvalidDataException("At least one reached place is needed.");
        }
    }
}
