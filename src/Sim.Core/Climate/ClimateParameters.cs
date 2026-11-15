using Sim.Core.Content;

namespace Sim.Core.Climate;

/// <summary>One randomly wandering part of a climate shift.</summary>
public sealed class NoiseComponent
{
    /// <summary>How long a swing typically lasts, in years.</summary>
    public double Years { get; set; }

    /// <summary>How large it typically is (standard deviation).</summary>
    public double Spread { get; set; }
}

/// <summary>How climate changes during a run, read from climate.json.</summary>
public sealed class ClimateParameters
{
    public int RegionGridFrequency { get; set; } = 4;

    public double BlendRadiusKm { get; set; } = 1000.0;

    public List<NoiseComponent> RegionTemperature { get; set; } = new List<NoiseComponent>();

    public List<NoiseComponent> RegionRainfall { get; set; } = new List<NoiseComponent>();

    public List<NoiseComponent> WorldTemperature { get; set; } = new List<NoiseComponent>();

    public double WorldTemperatureLimit { get; set; } = 2.0;

    public double RainfallFactorMin { get; set; } = 0.5;

    public double RainfallFactorMax { get; set; } = 1.5;

    public int BiomeIntervalYears { get; set; } = 10;

    public static ClimateParameters LoadDefault() => Parse(ContentFiles.ReadText("climate.json"));

    public static ClimateParameters Parse(string json)
    {
        ClimateParameters parameters = ContentFiles.Parse<ClimateParameters>(json, "climate");
        parameters.Validate();
        return parameters;
    }

    private void Validate()
    {
        if (RegionGridFrequency < 1 || RegionGridFrequency > 20)
        {
            throw new InvalidDataException("regionGridFrequency must be between 1 and 20.");
        }

        if (BlendRadiusKm <= 0.0)
        {
            throw new InvalidDataException("blendRadiusKm must be positive.");
        }

        foreach (NoiseComponent component in RegionTemperature.Concat(RegionRainfall).Concat(WorldTemperature))
        {
            if (component.Years < 1.0 || component.Spread < 0.0)
            {
                throw new InvalidDataException("Every climate component needs years of at least 1 and a spread of at least 0.");
            }
        }

        if (WorldTemperatureLimit < 0.0)
        {
            throw new InvalidDataException("worldTemperatureLimit cannot be negative.");
        }

        if (RainfallFactorMin <= 0.0 || RainfallFactorMin > 1.0 || RainfallFactorMax < 1.0)
        {
            throw new InvalidDataException("rainfallFactorMin must be above 0 and at most 1; rainfallFactorMax must be at least 1.");
        }

        if (BiomeIntervalYears < 1)
        {
            throw new InvalidDataException("biomeIntervalYears must be at least 1.");
        }
    }
}
