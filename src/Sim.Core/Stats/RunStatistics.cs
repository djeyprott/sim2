using System.Globalization;
using System.Text;
using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Goods;
using Sim.Core.Herding;
using Sim.Core.People;
using Sim.Core.Sea;
using Sim.Core.Settlements;
using Sim.Core.Technologies;

namespace Sim.Core.Stats;

/// <summary>
/// Yearly statistics of a run as CSV rows: world population, foragers, farmers, herders and
/// craftsmen, farming cells, settlements, the worldwide temperature shift, the share of land in
/// each biome, and for each good what was produced, consumed, spoiled, lost and traded since the
/// previous row and what is in store and in transit now, and for each technology the share of the
/// world's people who know it. Further tables list where and when each crop was first farmed, each
/// animal first herded, each technology discovered or lost, and each land reached by sea. Reads
/// the world state only.
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
    private readonly TechnologySystem? _technology;
    private readonly SeafaringSystem? _seafaring;

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
        _technology = simulation.Systems.OfType<TechnologySystem>().SingleOrDefault();
        _seafaring = simulation.Systems.OfType<SeafaringSystem>().SingleOrDefault();
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

        if (_technology != null)
        {
            columns.AddRange(_technology.Technologies.Technologies.Select(t => "know_" + t.Slug));
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

        if (_technology != null)
        {
            // People who know each technology (each group's share of its people), over all people.
            EntityTable table = _simulation.State.Table(PopulationGroups.TableName);
            double world = groups.WorldTotal();
            foreach (Technology t in _technology.Technologies.Technologies)
            {
                DoubleColumn known = table.DoubleColumn(PopulationGroups.KnowledgeColumnPrefix + t.Slug);
                double knowing = 0.0;
                for (int g = 0; g < table.IdCount; g++)
                {
                    if (groups.IsAlive(g))
                    {
                        knowing += groups.Size(g) * known[g];
                    }
                }

                values.Add((world > 0.0 ? knowing / world : 0.0).ToString("F4", CultureInfo.InvariantCulture));
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

    /// <summary>
    /// Each recorded technology event (first discovery, independent discovery at least the set
    /// distance from earlier ones, loss), as CSV with a header; for a loss, the people in contact.
    /// </summary>
    public string TechnologyEvents()
    {
        var text = new StringBuilder("technology,event,cell,latitude,longitude,year,date,contact\n");
        if (_technology == null)
        {
            return text.ToString();
        }

        foreach (TechnologyEvent e in _technology.Events())
        {
            var date = new GameDate(e.Tick);
            long year = date.IsBce ? -date.Year : date.Year;
            string kind = e.Kind switch
            {
                TechnologyEventKind.First => "first",
                TechnologyEventKind.Independent => "independent",
                _ => "lost",
            };
            text.Append(CultureInfo.InvariantCulture,
                $"\"{_technology.Technologies.Get(e.Technology).Name}\",{kind},{e.Cell},{_simulation.Map.Grid.Latitude(e.Cell):F2},{_simulation.Map.Grid.Longitude(e.Cell):F2},{year},{date},{Number(e.Contact)}\n");
        }

        return text.ToString();
    }

    /// <summary>Each landing by sea on land not reached at the start, as CSV with a header.</summary>
    public string Landings()
    {
        var text = new StringBuilder("cell,latitude,longitude,year,date,from_cell,from_latitude,from_longitude,distance_km,settlers\n");
        if (_seafaring == null)
        {
            return text.ToString();
        }

        var grid = _simulation.Map.Grid;
        foreach (Landing l in _seafaring.Landings())
        {
            var date = new GameDate(l.Tick);
            long year = date.IsBce ? -date.Year : date.Year;
            text.Append(CultureInfo.InvariantCulture,
                $"{l.Cell},{grid.Latitude(l.Cell):F2},{grid.Longitude(l.Cell):F2},{year},{date},{l.From},{grid.Latitude(l.From):F2},{grid.Longitude(l.From):F2},{grid.DistanceKm(l.From, l.Cell):F0},{Number(l.Settlers)}\n");
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
