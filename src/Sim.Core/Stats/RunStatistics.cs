using System.Globalization;
using System.Text;
using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.People;
using Sim.Core.Settlements;

namespace Sim.Core.Stats;

/// <summary>
/// Yearly statistics of a run as CSV rows: world population, foragers and farmers, farming
/// cells, settlements, the worldwide temperature shift and the share of land in each biome.
/// A second table lists where and when each crop was first farmed. Reads the world state only.
/// </summary>
public sealed class RunStatistics
{
    private readonly Simulation _simulation;
    private readonly ClimateSystem? _climate;
    private readonly ForagerSystem? _foragers;
    private readonly FarmingSystem? _farming;
    private readonly SettlementSystem? _settlements;
    private readonly int[] _land;

    public RunStatistics(Simulation simulation)
    {
        _simulation = simulation;
        _climate = simulation.Systems.OfType<ClimateSystem>().SingleOrDefault();
        _foragers = simulation.Systems.OfType<ForagerSystem>().SingleOrDefault();
        _farming = simulation.Systems.OfType<FarmingSystem>().SingleOrDefault();
        _settlements = simulation.Systems.OfType<SettlementSystem>().SingleOrDefault();
        _land = Enumerable.Range(0, simulation.Map.CellCount).Where(simulation.Map.IsLand).ToArray();
    }

    /// <summary>The column names of <see cref="Row"/>.</summary>
    public string Header()
    {
        var columns = new List<string>
        {
            "year", "date", "population", "foragers", "farmers", "farming_cells", "settlements", "world_temperature_shift",
        };
        if (_climate != null)
        {
            columns.AddRange(_climate.Biomes.Biomes.Select(b => "land_" + Slug(b.Name)));
        }

        return string.Join(",", columns);
    }

    /// <summary>One row for the simulation's state now. "year" is negative before the common era, with no year zero.</summary>
    public string Row()
    {
        GameDate date = _simulation.Date;
        long year = date.IsBce ? -date.Year : date.Year;
        var groups = new PopulationGroups(_simulation.State);
        double farmers = _farming?.WorldFarmers() ?? 0.0;
        double foragers = _foragers?.WorldPopulation() ?? 0.0;
        int farmingCells = 0;
        if (_farming != null)
        {
            int[] crop = _simulation.State.IntField(FarmingSystem.CropField);
            farmingCells = _land.Count(cell => crop[cell] > 0);
        }

        var values = new List<string>
        {
            year.ToString(CultureInfo.InvariantCulture),
            date.ToString(),
            Number(groups.WorldTotal()),
            Number(foragers),
            Number(farmers),
            farmingCells.ToString(CultureInfo.InvariantCulture),
            (_settlements?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            (_climate?.WorldTemperatureShift() ?? 0.0).ToString("F3", CultureInfo.InvariantCulture),
        };

        if (_climate != null)
        {
            int[] biome = _simulation.State.IntField(ClimateSystem.BiomeField);
            var counts = new int[_climate.Biomes.Count];
            foreach (int cell in _land)
            {
                if (biome[cell] >= 0)
                {
                    counts[biome[cell]]++;
                }
            }

            values.AddRange(counts.Select(c => (c / (double)_land.Length).ToString("F4", CultureInfo.InvariantCulture)));
        }

        return string.Join(",", values);
    }

    /// <summary>Where and when each crop was first farmed, as CSV with a header.</summary>
    public string Origins()
    {
        var text = new StringBuilder("crop,cell,latitude,longitude,year,date\n");
        if (_farming == null)
        {
            return text.ToString();
        }

        foreach ((int crop, int cell, long tick) in _farming.Origins())
        {
            var date = new GameDate(tick);
            long year = date.IsBce ? -date.Year : date.Year;
            text.Append(CultureInfo.InvariantCulture,
                $"\"{_farming.Crops.Get(crop).Name}\",{cell},{_simulation.Map.Grid.Latitude(cell):F2},{_simulation.Map.Grid.Longitude(cell):F2},{year},{date}\n");
        }

        return text.ToString();
    }

    private static string Number(double value) => Math.Round(value).ToString("F0", CultureInfo.InvariantCulture);

    private static string Slug(string name) =>
        new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
