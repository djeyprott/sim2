using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Map;

namespace Sim.Core.Climate;

/// <summary>
/// Changes climate at random during a run and assigns biomes from it.
/// Each land cell's current climate is its baseline from the world file, with its temperature
/// raised or lowered by <see cref="TemperatureShiftField"/> (the same amount every month) and
/// its rainfall multiplied by <see cref="RainfallFactorField"/>. Those come from a worldwide
/// temperature shift plus a blend of nearby climate regions, each of which wanders at random.
/// Runs yearly; biomes are recalculated every few years.
/// </summary>
public sealed class ClimateSystem : ISimSystem
{
    public const string SystemName = "climate";
    public const string TemperatureShiftField = "climate.temperatureShift";
    public const string RainfallFactorField = "climate.rainfallFactor";
    public const string BiomeField = "climate.biome";
    public const string RegionTableName = "climate.regions";
    public const string WorldTableName = "climate.world";

    /// <summary>Stored in the biome field for cells that are not land.</summary>
    public const int NoBiome = -1;

    private const int MaxBlend = GeodesicGrid.MaxNeighbours + 1;

    private readonly ClimateParameters _parameters;
    private readonly ComponentStep[] _temperatureSteps;
    private readonly ComponentStep[] _rainfallSteps;
    private readonly ComponentStep[] _worldSteps;

    private double[] _temperatureShift = Array.Empty<double>();
    private double[] _rainfallFactor = Array.Empty<double>();
    private int[] _biome = Array.Empty<int>();
    private DoubleColumn[] _regionTemperature = Array.Empty<DoubleColumn>();
    private DoubleColumn[] _regionRainfall = Array.Empty<DoubleColumn>();
    private DoubleColumn[] _worldTemperature = Array.Empty<DoubleColumn>();
    private EntityTable? _regions;
    private EntityTable? _world;

    private WorldMap? _map;
    private int _regionCount;
    private int[] _cells = Array.Empty<int>();
    private int[] _blendRegion = Array.Empty<int>();
    private double[] _blendWeight = Array.Empty<double>();
    private double[] _regionTotalTemperature = Array.Empty<double>();
    private double[] _regionTotalRainfall = Array.Empty<double>();

    // Baseline climate of each tracked land cell, by position in _cells. A temperature shift moves
    // every month by the same amount and a rainfall factor scales every month, so a cell's current
    // summary follows directly from these without going back to the monthly values.
    private double[] _baseMean = Array.Empty<double>();
    private double[] _baseWarmest = Array.Empty<double>();
    private double[] _baseColdest = Array.Empty<double>();
    private double[] _baseRain = Array.Empty<double>();
    private double[] _baseDriest = Array.Empty<double>();
    private double[] _summerShare = Array.Empty<double>();

    public ClimateSystem(ClimateParameters parameters, BiomeSet biomes)
    {
        _parameters = parameters;
        Biomes = biomes;
        _temperatureSteps = parameters.RegionTemperature.Select(c => new ComponentStep(c)).ToArray();
        _rainfallSteps = parameters.RegionRainfall.Select(c => new ComponentStep(c)).ToArray();
        _worldSteps = parameters.WorldTemperature.Select(c => new ComponentStep(c)).ToArray();
    }

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public BiomeSet Biomes { get; }

    public ClimateParameters Parameters => _parameters;

    /// <summary>The land cells whose climate this system tracks, in id order.</summary>
    public IReadOnlyList<int> LandCells => _cells;

    public static ClimateSystem CreateDefault() => new ClimateSystem(ClimateParameters.LoadDefault(), BiomeSet.LoadDefault());

    public void Register(WorldState state)
    {
        _temperatureShift = state.AddDoubleField(TemperatureShiftField);
        _rainfallFactor = state.AddDoubleField(RainfallFactorField);
        _biome = state.AddIntField(BiomeField);

        _regions = state.AddTable(RegionTableName);
        _regionTemperature = _temperatureSteps.Select((_, i) => _regions.AddDoubleColumn("temperature" + i)).ToArray();
        _regionRainfall = _rainfallSteps.Select((_, i) => _regions.AddDoubleColumn("rainfall" + i)).ToArray();

        _world = state.AddTable(WorldTableName);
        _worldTemperature = _worldSteps.Select((_, i) => _world.AddDoubleColumn("temperature" + i)).ToArray();
    }

    public void Initialize(SimContext context)
    {
        WorldMap map = context.Map;
        _map = map;

        GeodesicGrid regionGrid = GeodesicGrid.Create(_parameters.RegionGridFrequency);
        _regionCount = regionGrid.CellCount;
        for (int r = 0; r < _regionCount; r++)
        {
            _regions!.Add();
        }

        _world!.Add();
        _regionTotalTemperature = new double[_regionCount];
        _regionTotalRainfall = new double[_regionCount];

        var cells = new List<int>();
        for (int cell = 0; cell < map.CellCount; cell++)
        {
            if (map.IsLand(cell) && map.HasClimate(cell))
            {
                cells.Add(cell);
            }
        }

        _cells = cells.ToArray();
        _blendRegion = new int[_cells.Length * MaxBlend];
        _blendWeight = new double[_cells.Length * MaxBlend];
        Array.Fill(_blendRegion, -1);

        double radius = _parameters.BlendRadiusKm;
        for (int i = 0; i < _cells.Length; i++)
        {
            Vec3 point = map.Grid.Centre(_cells[i]);
            int nearest = regionGrid.FindCell(point);
            ReadOnlySpan<int> neighbours = regionGrid.Neighbours(nearest);
            double total = 0.0;
            for (int k = 0; k <= neighbours.Length; k++)
            {
                int region = k == 0 ? nearest : neighbours[k - 1];
                double km = GeodesicGrid.EarthRadiusKm * Vec3.AngleBetween(point, regionGrid.Centre(region));
                double weight = Math.Exp(-(km / radius) * (km / radius));
                _blendRegion[i * MaxBlend + k] = region;
                _blendWeight[i * MaxBlend + k] = weight;
                total += weight;
            }

            for (int k = 0; k <= neighbours.Length; k++)
            {
                _blendWeight[i * MaxBlend + k] /= total;
            }
        }

        _baseMean = new double[_cells.Length];
        _baseWarmest = new double[_cells.Length];
        _baseColdest = new double[_cells.Length];
        _baseRain = new double[_cells.Length];
        _baseDriest = new double[_cells.Length];
        _summerShare = new double[_cells.Length];
        Span<double> temperature = stackalloc double[12];
        Span<double> rainfall = stackalloc double[12];
        for (int i = 0; i < _cells.Length; i++)
        {
            int cell = _cells[i];
            for (int month = 0; month < 12; month++)
            {
                temperature[month] = map.TemperatureC(cell, month);
                rainfall[month] = map.RainfallMm(cell, month);
            }

            ClimateSummary baseline = ClimateSummary.FromMonths(temperature, rainfall, map.Grid.Latitude(cell) >= 0.0);
            _baseMean[i] = baseline.Mean;
            _baseWarmest[i] = baseline.Warmest;
            _baseColdest[i] = baseline.Coldest;
            _baseRain[i] = baseline.Rain;
            _baseDriest[i] = baseline.Driest;
            _summerShare[i] = baseline.SummerShare;
        }

        Array.Fill(_biome, NoBiome);
        foreach (int cell in _cells)
        {
            _temperatureShift[cell] = 0.0;
            _rainfallFactor[cell] = 1.0;
        }

        RecalculateBiomes();
    }

    public void Update(SimContext context)
    {
        Rng rng = context.Rng;
        for (int r = 0; r < _regionCount; r++)
        {
            double totalTemperature = 0.0;
            for (int k = 0; k < _temperatureSteps.Length; k++)
            {
                double value = _temperatureSteps[k].Next(_regionTemperature[k][r], rng);
                _regionTemperature[k][r] = value;
                totalTemperature += value;
            }

            double totalRainfall = 0.0;
            for (int k = 0; k < _rainfallSteps.Length; k++)
            {
                double value = _rainfallSteps[k].Next(_regionRainfall[k][r], rng);
                _regionRainfall[k][r] = value;
                totalRainfall += value;
            }

            _regionTotalTemperature[r] = totalTemperature;
            _regionTotalRainfall[r] = totalRainfall;
        }

        for (int k = 0; k < _worldSteps.Length; k++)
        {
            _worldTemperature[k][0] = _worldSteps[k].Next(_worldTemperature[k][0], rng);
        }

        double world = WorldTemperatureShift();
        double minFactor = _parameters.RainfallFactorMin;
        double maxFactor = _parameters.RainfallFactorMax;
        for (int i = 0; i < _cells.Length; i++)
        {
            double temperature = 0.0;
            double rainfall = 0.0;
            int start = i * MaxBlend;
            for (int k = 0; k < MaxBlend; k++)
            {
                int region = _blendRegion[start + k];
                if (region < 0)
                {
                    break;
                }

                double weight = _blendWeight[start + k];
                temperature += weight * _regionTotalTemperature[region];
                rainfall += weight * _regionTotalRainfall[region];
            }

            int cell = _cells[i];
            _temperatureShift[cell] = world + temperature;
            _rainfallFactor[cell] = Math.Clamp(Math.Exp(rainfall), minFactor, maxFactor);
        }

        long year = context.Tick / GameDate.MonthsPerYear;
        if (year % _parameters.BiomeIntervalYears == 0)
        {
            RecalculateBiomes();
        }
    }

    /// <summary>The worldwide temperature shift from baseline, in degrees C.</summary>
    public double WorldTemperatureShift()
    {
        double total = 0.0;
        foreach (DoubleColumn column in _worldTemperature)
        {
            total += column[0];
        }

        double limit = _parameters.WorldTemperatureLimit;
        return Math.Clamp(total, -limit, limit);
    }

    /// <summary>A land cell's current mean temperature for a month (0 is January), in degrees C.</summary>
    public double CurrentTemperatureC(int cell, int month) => _map!.TemperatureC(cell, month) + _temperatureShift[cell];

    /// <summary>A land cell's current rainfall for a month (0 is January), in mm.</summary>
    public double CurrentRainfallMm(int cell, int month) => _map!.RainfallMm(cell, month) * _rainfallFactor[cell];

    /// <summary>A summary of a land cell's current climate.</summary>
    public ClimateSummary CurrentClimate(int cell)
    {
        Span<double> temperature = stackalloc double[12];
        Span<double> rainfall = stackalloc double[12];
        for (int month = 0; month < 12; month++)
        {
            temperature[month] = CurrentTemperatureC(cell, month);
            rainfall[month] = CurrentRainfallMm(cell, month);
        }

        return ClimateSummary.FromMonths(temperature, rainfall, _map!.Grid.Latitude(cell) >= 0.0);
    }

    private void RecalculateBiomes()
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            int cell = _cells[i];
            double shift = _temperatureShift[cell];
            double factor = _rainfallFactor[cell];
            var climate = new ClimateSummary(
                _baseMean[i] + shift,
                _baseWarmest[i] + shift,
                _baseColdest[i] + shift,
                _baseRain[i] * factor,
                _baseDriest[i] * factor,
                _summerShare[i]);
            _biome[cell] = Biomes.Classify(climate);
        }
    }

    // One step of a value that wanders at random and drifts back toward zero, keeping a steady spread.
    private readonly struct ComponentStep
    {
        private readonly double _keep;
        private readonly double _noise;

        public ComponentStep(NoiseComponent component)
        {
            _keep = Math.Exp(-1.0 / component.Years);
            _noise = Math.Sqrt(1.0 - _keep * _keep) * component.Spread;
        }

        public double Next(double value, Rng rng) => _keep * value + _noise * rng.NextGaussian();
    }
}
