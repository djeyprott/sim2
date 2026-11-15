using System.Text.Json;

namespace Sim.Core.Content;

/// <summary>
/// The simulation's content files (data/content/*.json in the repository), built into the core
/// library so the simulation and the viewer always use the same version.
/// </summary>
public static class ContentFiles
{
    internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The text of a content file, by file name, for example "biomes.json".</summary>
    public static string ReadText(string fileName)
    {
        string resource = "Sim.Content." + fileName;
        using Stream stream = typeof(ContentFiles).Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"Content file '{fileName}' is not built into the program.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Parses a content file's text into the given type, with comments allowed.</summary>
    internal static T Parse<T>(string json, string what)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidDataException($"The {what} file is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"The {what} file could not be read: {error.Message}", error);
        }
    }
}
