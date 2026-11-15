using Sim.Core.Content;

namespace Sim.Core.People;

public enum SocialClass
{
    Lower = 0,
    Middle = 1,
    Upper = 2,
}

/// <summary>A way of life a population group can have.</summary>
public sealed class Occupation
{
    internal Occupation(int index, string name, SocialClass socialClass, bool livesInSettlement)
    {
        Index = index;
        Name = name;
        Class = socialClass;
        LivesInSettlement = livesInSettlement;
    }

    public int Index { get; }

    public string Name { get; }

    public SocialClass Class { get; }

    /// <summary>True if the group lives in its cell's settlement, false if in the countryside.</summary>
    public bool LivesInSettlement { get; }
}

/// <summary>The list of occupations, read from occupations.json.</summary>
public sealed class OccupationSet
{
    public const string Forager = "Forager";
    public const string Farmer = "Farmer";

    private readonly Occupation[] _occupations;

    private OccupationSet(Occupation[] occupations)
    {
        _occupations = occupations;
    }

    public IReadOnlyList<Occupation> Occupations => _occupations;

    public int Count => _occupations.Length;

    public static OccupationSet LoadDefault() => Parse(ContentFiles.ReadText("occupations.json"));

    public static OccupationSet Parse(string json)
    {
        OccupationFile file = ContentFiles.Parse<OccupationFile>(json, "occupations");
        if (file.Occupations.Count == 0 || file.Occupations.Count > 1000)
        {
            throw new InvalidDataException("The occupations file must list between 1 and 1000 occupations.");
        }

        var occupations = new Occupation[file.Occupations.Count];
        for (int i = 0; i < occupations.Length; i++)
        {
            OccupationEntry entry = file.Occupations[i];
            if (string.IsNullOrWhiteSpace(entry.Name) || occupations.Take(i).Any(o => o.Name == entry.Name))
            {
                throw new InvalidDataException($"Occupation {i + 1} has no name or repeats another's.");
            }

            SocialClass socialClass = entry.Class.ToLowerInvariant() switch
            {
                "lower" => SocialClass.Lower,
                "middle" => SocialClass.Middle,
                "upper" => SocialClass.Upper,
                _ => throw new InvalidDataException($"Occupation '{entry.Name}' has class '{entry.Class}'; use lower, middle or upper."),
            };

            bool inSettlement = entry.LivesIn.ToLowerInvariant() switch
            {
                "countryside" => false,
                "settlement" => true,
                _ => throw new InvalidDataException($"Occupation '{entry.Name}' lives in '{entry.LivesIn}'; use countryside or settlement."),
            };

            occupations[i] = new Occupation(i, entry.Name, socialClass, inSettlement);
        }

        return new OccupationSet(occupations);
    }

    public Occupation Get(int index) => _occupations[index];

    public int IndexOf(string name)
    {
        foreach (Occupation occupation in _occupations)
        {
            if (occupation.Name == name)
            {
                return occupation.Index;
            }
        }

        throw new KeyNotFoundException($"There is no occupation named '{name}'.");
    }

    private sealed class OccupationFile
    {
        public List<OccupationEntry> Occupations { get; set; } = new List<OccupationEntry>();
    }

    private sealed class OccupationEntry
    {
        public string Name { get; set; } = "";

        public string Class { get; set; } = "";

        public string LivesIn { get; set; } = "";
    }
}
