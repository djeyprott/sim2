using System.Text;
using Sim.Core.Content;
using Sim.Core.Engine;

namespace Sim.Core.Settlements;

/// <summary>When settlements are founded and abandoned, and how they are named, read from settlements.json.</summary>
public sealed class SettlementParameters
{
    public double FoundAtFarmers { get; set; } = 1000.0;

    public double AbandonBelowFarmers { get; set; } = 100.0;

    public double SettledShare { get; set; } = 0.15;

    public int MinSyllables { get; set; } = 2;

    public int MaxSyllables { get; set; } = 3;

    public List<string> Onsets { get; set; } = new List<string>();

    public List<string> Vowels { get; set; } = new List<string>();

    public List<string> Codas { get; set; } = new List<string>();

    public static SettlementParameters LoadDefault() => Parse(ContentFiles.ReadText("settlements.json"));

    public static SettlementParameters Parse(string json)
    {
        SettlementParameters p = ContentFiles.Parse<SettlementParameters>(json, "settlements");
        if (p.FoundAtFarmers <= 0.0 || p.AbandonBelowFarmers < 0.0 || p.AbandonBelowFarmers >= p.FoundAtFarmers)
        {
            throw new InvalidDataException("Settlements need 0 <= abandonBelowFarmers < foundAtFarmers.");
        }

        if (p.SettledShare < 0.0 || p.SettledShare > 1.0 || double.IsNaN(p.SettledShare))
        {
            throw new InvalidDataException("settledShare must be between 0 and 1.");
        }

        if (p.MinSyllables < 1 || p.MaxSyllables < p.MinSyllables || p.MaxSyllables > 6)
        {
            throw new InvalidDataException("Settlement names need 1 <= minSyllables <= maxSyllables <= 6.");
        }

        if (p.Onsets.Count == 0 || p.Vowels.Count == 0 || p.Codas.Count == 0 || p.Vowels.Any(string.IsNullOrEmpty))
        {
            throw new InvalidDataException("Settlement names need onsets, codas and non-empty vowels.");
        }

        return p;
    }

    /// <summary>A placeholder name drawn from the syllable lists.</summary>
    public string MakeName(Rng rng)
    {
        int syllables = rng.NextInt(MinSyllables, MaxSyllables + 1);
        var name = new StringBuilder();
        for (int i = 0; i < syllables; i++)
        {
            name.Append(Onsets[rng.NextInt(Onsets.Count)]);
            name.Append(Vowels[rng.NextInt(Vowels.Count)]);
            // Only the last syllable may end in a coda more than half the time, which keeps names pronounceable.
            if (i == syllables - 1 || rng.Chance(0.3))
            {
                name.Append(Codas[rng.NextInt(Codas.Count)]);
            }
        }

        name[0] = char.ToUpperInvariant(name[0]);
        return name.ToString();
    }
}
