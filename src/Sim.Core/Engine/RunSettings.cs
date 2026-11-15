using System.Globalization;

namespace Sim.Core.Engine;

/// <summary>Where farming's crops can first be domesticated.</summary>
public enum CropHomelands
{
    /// <summary>Each crop group starts where it grew wild on the real Earth.</summary>
    Real = 0,

    /// <summary>Each crop group starts in a random region whose climate suits it.</summary>
    Random = 1,
}

/// <summary>
/// The choices made when a run starts. They are stored in the world state's settings, so they
/// travel with a save and systems can read them in <see cref="ISimSystem.Initialize"/>.
/// </summary>
public sealed class RunSettings
{
    public const string SeedKey = "run.seed";
    public const string CropHomelandsKey = "run.cropHomelands";

    public ulong Seed { get; init; } = 1;

    public CropHomelands CropHomelands { get; init; } = CropHomelands.Real;

    /// <summary>Reads settings from a world state. Missing values take their defaults.</summary>
    public static RunSettings ReadFrom(WorldState state)
    {
        ulong seed = 1;
        string? seedText = state.GetSetting(SeedKey);
        if (seedText != null && !ulong.TryParse(seedText, NumberStyles.None, CultureInfo.InvariantCulture, out seed))
        {
            throw new InvalidDataException($"The run's seed '{seedText}' is not a whole number.");
        }

        CropHomelands homelands = CropHomelands.Real;
        string? homelandsText = state.GetSetting(CropHomelandsKey);
        if (homelandsText != null && !TryParseHomelands(homelandsText, out homelands))
        {
            throw new InvalidDataException($"The run's crop homelands setting '{homelandsText}' is not known.");
        }

        return new RunSettings { Seed = seed, CropHomelands = homelands };
    }

    /// <summary>Accepts "real" or "random", in any letter case.</summary>
    public static bool TryParseHomelands(string text, out CropHomelands homelands)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "real":
                homelands = CropHomelands.Real;
                return true;
            case "random":
                homelands = CropHomelands.Random;
                return true;
            default:
                homelands = CropHomelands.Real;
                return false;
        }
    }

    public static string HomelandsText(CropHomelands homelands) => homelands == CropHomelands.Random ? "random" : "real";

    public void WriteTo(WorldState state)
    {
        state.SetSetting(SeedKey, Seed.ToString(CultureInfo.InvariantCulture));
        state.SetSetting(CropHomelandsKey, HomelandsText(CropHomelands));
    }

    /// <summary>A short description for display, such as "Seed 42, real crop homelands".</summary>
    public string Describe() =>
        $"Seed {Seed.ToString(CultureInfo.InvariantCulture)}, {HomelandsText(CropHomelands)} crop homelands";
}
