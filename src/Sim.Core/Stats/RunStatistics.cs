using System.Globalization;
using System.Text;
using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Goods;
using Sim.Core.Herding;
using Sim.Core.People;
using Sim.Core.Polities;
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
    private readonly PolitySystem? _polities;

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
        _polities = simulation.Systems.OfType<PolitySystem>().SingleOrDefault();
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

        if (_polities != null)
        {
            columns.AddRange(new[] { "chiefdoms", "states", "people_ruled", "largest_polity_people", "largest_polity_km2", "elite", "soldiers", "unrest_ruled", "tribute_grain", "wars", "wars_started", "conquests", "war_deaths" });
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

        if (_polities != null)
        {
            // Polities standing, the people they rule, and the largest by people (with its area).
            List<PolityInfo> standing = _polities.Standing();
            int top = standing.Count == 0 ? 0 : standing.Max(p => p.Id) + 1;
            var people = new double[top];
            var area = new double[top];
            foreach (int cell in _land)
            {
                int ruler = _polities.RulerOf(cell);
                if (ruler >= 0)
                {
                    people[ruler] += groups.CellTotal(cell);
                    area[ruler] += _simulation.Map.Grid.AreaKm2(cell);
                }
            }

            int largest = -1;
            for (int id = 0; id < top; id++)
            {
                if (largest < 0 || people[id] > people[largest])
                {
                    largest = id;
                }
            }

            values.Add(standing.Count(p => p.Level == PolityLevel.Chiefdom).ToString(CultureInfo.InvariantCulture));
            values.Add(standing.Count(p => p.Level == PolityLevel.State).ToString(CultureInfo.InvariantCulture));
            values.Add(Number(people.Sum()));
            values.Add(Number(largest >= 0 ? people[largest] : 0.0));
            values.Add(Number(largest >= 0 ? area[largest] : 0.0));

            // Elite and soldiers, the mean unrest of the people ruled, and grain taken in tribute.
            values.Add(Number(_occupations.Contains(OccupationSet.Elite) ? groups.WorldTotal(_occupations.IndexOf(OccupationSet.Elite)) : 0.0));
            values.Add(Number(_occupations.Contains(OccupationSet.Soldier) ? groups.WorldTotal(_occupations.IndexOf(OccupationSet.Soldier)) : 0.0));
            double ruledPeople = 0.0, ruledUnrest = 0.0;
            foreach (int cell in _land)
            {
                if (_polities.RulerOf(cell) >= 0)
                {
                    double here = groups.CellTotal(cell);
                    ruledPeople += here;
                    ruledUnrest += here * _polities.CellUnrest(cell);
                }
            }

            values.Add((ruledPeople > 0.0 ? ruledUnrest / ruledPeople : 0.0).ToString("F4", CultureInfo.InvariantCulture));
            values.Add(Number(standing.Sum(p => _polities.Tribute(p.Id))));

            // Wars under way, wars started and conquests so far, and everyone killed in war so far.
            List<WarInfo> wars = _polities.Wars();
            values.Add(wars.Count(w => w.UnderWay).ToString(CultureInfo.InvariantCulture));
            values.Add(wars.Count.ToString(CultureInfo.InvariantCulture));
            values.Add(wars.Count(w => w.Conquest).ToString(CultureInfo.InvariantCulture));
            values.Add(Number(wars.Sum(w => w.AttackerDeaths + w.DefenderDeaths)));
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

    // Broad regions for the regional table, first match wins: name, latitude from and to, longitude from and to.
    private static readonly (string Name, double LatFrom, double LatTo, double LonFrom, double LonTo)[] RegionBoxes =
    {
        ("Americas", -90, 90, -180, -30),
        ("Egypt and Nile", 15, 32, 24, 36),
        ("Southwest Asia", 12, 42, 26, 63),
        ("Europe", 35, 72, -30, 40),
        ("Siberia", 56, 90, 40, 180),
        ("Steppe and Central Asia", 35, 56, 40, 95),
        ("South Asia", 5, 37, 60, 95),
        ("East Asia", 18, 56, 95, 150),
        ("Southeast Asia", -12, 28, 92, 180),
        ("North Africa and Sahara", 18, 38, -30, 40),
        ("Sahel", 10, 18, -30, 52),
        ("West and Central Africa", -10, 10, -30, 30),
        ("East and southern Africa", -40, 18, -30, 60),
        ("Australia and Pacific", -90, 0, 100, 180),
    };

    /// <summary>The broad region a cell lies in (the regional table's names), or "Other".</summary>
    public string RegionOf(int cell)
    {
        double lat = _simulation.Map.Grid.Latitude(cell), lon = _simulation.Map.Grid.Longitude(cell);
        foreach (var box in RegionBoxes)
        {
            if (lat >= box.LatFrom && lat < box.LatTo && lon >= box.LonFrom && lon < box.LonTo)
            {
                return box.Name;
            }
        }

        return "Other";
    }

    /// <summary>The column names of <see cref="RegionRows"/>.</summary>
    public string RegionHeader()
    {
        var columns = new List<string> { "year", "region", "population", "farmers", "herders", "craftsmen", "settlements" };
        if (_technology != null)
        {
            foreach (Technology t in _technology.Technologies.Technologies)
            {
                columns.Add("know_" + t.Slug);
                columns.Add("use_" + t.Slug);
                columns.Add("findable_" + t.Slug);
            }
        }

        if (_stockpiles != null)
        {
            columns.AddRange(_stockpiles.Goods.Goods.Select(g => g.Slug + "_in_store"));
            columns.AddRange(MadeGoods().Select(g => g.Slug + "_made"));
            if (_technology != null)
            {
                columns.AddRange(new[] { "bronze_settlements", "bronze_settlements_with_tin", "bronze_settlements_with_copper", "bronze_settlements_with_both" });
            }
        }

        return string.Join(",", columns);
    }

    // Goods craftsmen make (those with a "made last January" field).
    private List<Good> MadeGoods() => _stockpiles == null ? new List<Good>()
        : _stockpiles.Goods.Goods.Where(g => _simulation.State.HasField(CraftSystem.MadeFieldPrefix + g.Slug)).ToList();

    /// <summary>
    /// One row per broad region (see <see cref="RegionOf"/>) for the state now: people by
    /// occupation, settlements; for each technology the share of the region's people who know it
    /// and the share in groups that use it (at least the in-use share knowing it, where the cell
    /// meets its conditions), and the farmed cells where it could be discovered now; what is in store of each good; what its craftsmen made last January;
    /// and how many settlements have a group using bronze, and of those how many hold tin, copper and both. Each row ends with a newline.
    /// </summary>
    public string RegionRows()
    {
        GameDate date = _simulation.Date;
        long year = date.IsBce ? -date.Year : date.Year;
        var names = RegionBoxes.Select(b => b.Name).Append("Other").ToList();
        int regions = names.Count;
        var groups = new PopulationGroups(_simulation.State);
        int techs = _technology?.Technologies.Count ?? 0;
        int goods = _stockpiles?.Goods.Count ?? 0;
        var people = new double[regions, 4];
        var settlements = new int[regions];
        var know = new double[regions, techs];
        var use = new double[regions, techs];
        var findable = new int[regions, techs];
        var store = new double[regions, goods];
        List<Good> madeGoods = MadeGoods();
        double[][] madeFields = madeGoods.Select(g => _simulation.State.DoubleField(CraftSystem.MadeFieldPrefix + g.Slug)).ToArray();
        var made = new double[regions, madeGoods.Count];
        var bronze = new int[regions, 4];
        var regionOfCell = new Dictionary<int, int>();
        int Region(int cell)
        {
            if (!regionOfCell.TryGetValue(cell, out int r))
            {
                r = names.IndexOf(RegionOf(cell));
                regionOfCell[cell] = r;
            }

            return r;
        }

        int[] occupationColumn =
        {
            _occupations.Contains(OccupationSet.Farmer) ? _occupations.IndexOf(OccupationSet.Farmer) : -1,
            _occupations.Contains(OccupationSet.Herder) ? _occupations.IndexOf(OccupationSet.Herder) : -1,
            _occupations.Contains(OccupationSet.Craftsman) ? _occupations.IndexOf(OccupationSet.Craftsman) : -1,
        };
        EntityTable table = _simulation.State.Table(PopulationGroups.TableName);
        DoubleColumn[] known = Enumerable.Range(0, techs)
            .Select(t => table.DoubleColumn(PopulationGroups.KnowledgeColumnPrefix + _technology!.Technologies.Get(t).Slug)).ToArray();
        double inUse = _technology?.Parameters.InUse ?? 0.5;
        for (int g = 0; g < groups.IdCount; g++)
        {
            if (!groups.IsAlive(g) || groups.Size(g) <= 0.0)
            {
                continue;
            }

            int cell = groups.Cell(g);
            int r = Region(cell);
            double size = groups.Size(g);
            people[r, 0] += size;
            for (int k = 0; k < 3; k++)
            {
                if (groups.Occupation(g) == occupationColumn[k])
                {
                    people[r, k + 1] += size;
                }
            }

            for (int t = 0; t < techs; t++)
            {
                know[r, t] += size * known[t][g];
                if (known[t][g] >= inUse && _technology!.CellAllows(cell, t))
                {
                    use[r, t] += size;
                }
            }
        }

        // Cells with farmers where each technology could be discovered now: its conditions met
        // (goods near in the cell itself) and farming has stood long enough.
        TechnologyConditions? conditions = techs > 0 ? TechnologyUse.TryCreate(_simulation.State, _simulation.Map)?.Conditions : null;
        if (conditions != null && occupationColumn[0] >= 0)
        {
            foreach (int cell in _land)
            {
                int farmerGroup = groups.Find(cell, occupationColumn[0]);
                if (farmerGroup < 0 || groups.Size(farmerGroup) <= 0.0)
                {
                    continue;
                }

                int r = Region(cell);
                for (int t = 0; t < techs; t++)
                {
                    if (conditions.Allows(cell, t) && conditions.NearForDiscovery(cell, t, _simulation.Tick))
                    {
                        findable[r, t]++;
                    }
                }
            }
        }

        int bronzeTech = -1, tin = -1, copper = -1;
        if (_technology != null && _stockpiles != null)
        {
            bronzeTech = _technology.Technologies.Technologies.ToList().FindIndex(t => t.Name == "Bronze");
            tin = _stockpiles.Goods.Goods.ToList().FindIndex(g => g.Name == "Tin");
            copper = _stockpiles.Goods.Goods.ToList().FindIndex(g => g.Name == "Copper");
        }

        if (_settlements != null && _simulation.State.HasTable(SettlementSystem.TableName))
        {
            EntityTable settlementTable = _simulation.State.Table(SettlementSystem.TableName);
            IntColumn cellOf = settlementTable.IntColumn("cell");
            for (int id = 0; id < settlementTable.IdCount; id++)
            {
                if (!settlementTable.IsAlive(id))
                {
                    continue;
                }

                int cell = cellOf[id];
                int r = Region(cell);
                settlements[r]++;
                for (int good = 0; good < goods; good++)
                {
                    store[r, good] += _stockpiles!.Stock(cell, good);
                }

                for (int k = 0; k < madeFields.Length; k++)
                {
                    made[r, k] += madeFields[k][cell];
                }

                if (bronzeTech < 0)
                {
                    continue;
                }

                bool usesBronze = false;
                foreach (int g in groups.InCell(cell))
                {
                    usesBronze |= groups.Size(g) > 0.0 && known[bronzeTech][g] >= inUse;
                }

                if (usesBronze)
                {
                    bronze[r, 0]++;
                    if (tin >= 0 && _stockpiles!.Stock(cell, tin) > 0.0)
                    {
                        bronze[r, 1]++;
                    }

                    bool hasTin = tin >= 0 && _stockpiles!.Stock(cell, tin) > 0.0;
                    bool hasCopper = copper >= 0 && _stockpiles!.Stock(cell, copper) > 0.0;
                    bronze[r, 2] += hasCopper ? 1 : 0;
                    bronze[r, 3] += hasTin && hasCopper ? 1 : 0;
                }
            }
        }

        var text = new StringBuilder();
        for (int r = 0; r < regions; r++)
        {
            if (people[r, 0] <= 0.0)
            {
                continue;
            }

            var values = new List<string> { year.ToString(CultureInfo.InvariantCulture), names[r] };
            for (int k = 0; k < 4; k++)
            {
                values.Add(Number(people[r, k]));
            }

            values.Add(settlements[r].ToString(CultureInfo.InvariantCulture));
            for (int t = 0; t < techs; t++)
            {
                values.Add((know[r, t] / people[r, 0]).ToString("F4", CultureInfo.InvariantCulture));
                values.Add((use[r, t] / people[r, 0]).ToString("F4", CultureInfo.InvariantCulture));
                values.Add(findable[r, t].ToString(CultureInfo.InvariantCulture));
            }

            if (_stockpiles != null)
            {
                for (int good = 0; good < goods; good++)
                {
                    values.Add(Amount(store[r, good]));
                }

                for (int k = 0; k < madeGoods.Count; k++)
                {
                    values.Add(Amount(made[r, k]));
                }

                if (_technology != null)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        values.Add(bronze[r, k].ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            text.Append(string.Join(",", values)).Append('\n');
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
