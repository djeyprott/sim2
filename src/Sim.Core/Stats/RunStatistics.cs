using System.Globalization;
using System.Text;
using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Goods;
using Sim.Core.Herding;
using Sim.Core.People;
using Sim.Core.Settlements;

namespace Sim.Core.Stats;

/// <summary>
/// Yearly statistics of a run as CSV rows: world population, foragers, farmers, herders and
/// craftsmen, farming cells, settlements, the worldwide temperature shift, the share of land in
/// each biome, and for each good what was produced, consumed, spoiled, lost and traded since the
/// previous row and what is in store and in transit now. Further tables list where and when each
/// crop was first farmed and each animal first herded. Reads the world state only.
/// </summary>
public sealed class RunStatistics
{
    private readonly Simulation _simulation;
    private readonly ClimateSystem? _climate;
    private readonly ForagerSystem? _foragers;
    private readonly FarmingSystem? _farming;
    private readonly SettlementSystem? _settlements;
    private readonly HerdingSystem? _herding;
    private readonly int[] _land;
    private readonly OccupationSet _occupations = OccupationSet.LoadDefault();
    private readonly Stockpiles? _stockpiles;

    // Ledger totals at the previous row, by good: produced, consumed, spoiled, lost, traded.
    private double[,] _previous = new double[0, 5];

    public RunStatistics(Simulation simulation)
    {
        _simulation = simulation;
        _climate = simulation.Systems.OfType<ClimateSystem>().SingleOrDefault();
        _foragers = simulation.Systems.OfType<ForagerSystem>().SingleOrDefault();
        _farming = simulation.Systems.OfType<FarmingSystem>().SingleOrDefault();
        _settlements = simulation.Systems.OfType<SettlementSystem>().SingleOrDefault();
        _herding = simulation.Systems.OfType<HerdingSystem>().SingleOrDefault();
        if (simulation.State.HasTable(Stockpiles.LedgerTableName))
        {
            _stockpiles = new Stockpiles(simulation.State, GoodSet.LoadDefault());
            _previous = new double[_stockpiles.Goods.Count, 5];
        }
        _land = Enumerable.Range(0, simulation.Map.CellCount).Where(simulation.Map.IsLand).ToArray();
    }

    /// <summary>The column names of <see cref="Row"/>.</summary>
    public string Header()
    {
        var columns = new List<string>
        {
            "year", "date", "population", "foragers", "farmers", "herders", "craftsmen", "farming_cells", "settlements", "world_temperature_shift",
        };
        if (_climate != null)
        {
            columns.AddRange(_climate.Biomes.Biomes.Select(b => "land_" + Slug(b.Name)));
        }

        if (_stockpiles != null)
        {
            foreach (Good good in _stockpiles.Goods.Goods)
            {
                columns.AddRange(new[] { "produced", "consumed", "spoiled", "lost", "traded", "in_store", "in_transit" }
                    .Select(what => $"{good.Slug}_{what}"));
            }
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
            Number(Occupation(groups, OccupationSet.Herder)),
            Number(Occupation(groups, OccupationSet.Craftsman)),
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

        if (_stockpiles != null)
        {
            Stockpiles s = _stockpiles;
            for (int g = 0; g < s.Goods.Count; g++)
            {
                double[] totals = { s.Produced(g), s.Consumed(g), s.Spoiled(g), s.Lost(g), s.Traded(g) };
                for (int k = 0; k < totals.Length; k++)
                {
                    values.Add(Amount(totals[k] - _previous[g, k]));
                    _previous[g, k] = totals[k];
                }

                values.Add(Amount(s.InStore(g)));
                values.Add(Amount(s.InTransit(g)));
            }
        }

        return string.Join(",", values);
    }

    private double Occupation(PopulationGroups groups, string name) =>
        _occupations.Contains(name) ? groups.WorldTotal(_occupations.IndexOf(name)) : 0.0;

    /// <summary>Where and when each animal was first herded in one of its homelands, as CSV with a header.</summary>
    public string HerdingOrigins()
    {
        var text = new StringBuilder("animal,homeland,cell,latitude,longitude,year,date\n");
        if (_herding == null)
        {
            return text.ToString();
        }

        foreach ((int animal, int homeland, int cell, long tick) in _herding.Origins())
        {
            var date = new GameDate(tick);
            long year = date.IsBce ? -date.Year : date.Year;
            text.Append(CultureInfo.InvariantCulture,
                $"\"{_herding.Livestock.Get(animal).Name}\",{homeland},{cell},{_simulation.Map.Grid.Latitude(cell):F2},{_simulation.Map.Grid.Longitude(cell):F2},{year},{date}\n");
        }

        return text.ToString();
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

    // Goods amounts: small ones (obsidian, salt) need decimals.
    private static string Amount(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Slug(string name) =>
        new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
