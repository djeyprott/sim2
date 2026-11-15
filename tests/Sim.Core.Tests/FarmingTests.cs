using Sim.Core.Climate;
using Sim.Core.Content;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Goods;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Settlements;
using Xunit;

namespace Sim.Core.Tests;

public class CropTests
{
    [Fact]
    public void Crops_load_with_about_eight_groups()
    {
        CropSet crops = CropSet.LoadDefault();
        Assert.InRange(crops.Count, 6, 12);
        Assert.Equal(0, crops.IndexOf("Wheat and barley"));
        Assert.All(crops.Crops, c => Assert.True(c.Warmest[0] >= 10.0));
        FarmingParameters parameters = FarmingParameters.LoadDefault();
        Assert.Contains("Desert", parameters.NoFarmingBiomes);
    }

    [Fact]
    public void Suitability_follows_the_ranges()
    {
        Crop wheat = CropSet.LoadDefault().Get(0);
        var mild = new ClimateSummary(15, 25, 5, 500, 10, 0.3);
        var frozen = new ClimateSummary(-10, 8, -30, 500, 10, 0.5);
        Assert.Equal(1.0, wheat.Suitability(mild, 500), 9);
        Assert.Equal(0.0, wheat.Suitability(frozen, 500));
        Assert.Equal(0.0, wheat.Suitability(mild, 100));
        Assert.Equal(0.5, wheat.Suitability(mild, 275), 9);
    }

    [Fact]
    public void Bad_crop_files_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => CropSet.Parse(
            "{ \"crops\": [ { \"name\": \"A\", \"colour\": \"#000000\", \"homeland\": { \"lat\": 0, \"lon\": 0, \"radiusKm\": 100 }, "
            + "\"warmest\": [5, 10, 20, 30], \"coldest\": [0, 1, 2, 3], \"water\": [0, 1, 2, 3] } ] }"));
        Assert.Throws<InvalidDataException>(() => CropSet.Parse(
            "{ \"crops\": [ { \"name\": \"A\", \"colour\": \"#000000\", \"homeland\": { \"lat\": 0, \"lon\": 0, \"radiusKm\": 100 }, "
            + "\"warmest\": [12, 10, 20, 30], \"coldest\": [0, 1, 2, 3], \"water\": [0, 1, 2, 3] } ] }"));
    }
}

public class FarmingTests : IClassFixture<EarthWorldFixture>
{
    private readonly WorldMap _world;

    public FarmingTests(EarthWorldFixture fixture)
    {
        _world = fixture.World;
    }

    // A simulation where farming is first taken up almost at once, so short runs show it.
    private Simulation FastFarming(ulong seed, CropHomelands homelands = CropHomelands.Real, double harvestSpread = -1.0)
    {
        ClimateSystem climate = ClimateSystem.CreateDefault();
        OccupationSet occupations = OccupationSet.LoadDefault();
        FarmingParameters parameters = FarmingParameters.LoadDefault();
        parameters.InventionChance = 0.02;
        if (harvestSpread >= 0.0)
        {
            parameters.HarvestSpread = harvestSpread;
        }
        var systems = new ISimSystem[]
        {
            climate,
            new PopulationSystem(),
            GoodsSystem.CreateDefault(),
            ForagerSystem.CreateDefault(climate.Biomes, occupations),
            new FarmingSystem(parameters, CropSet.LoadDefault(), climate.Biomes, occupations),
            SettlementSystem.CreateDefault(occupations),
            ResourceSystem.CreateDefault(climate.Biomes, occupations),
            CraftSystem.CreateDefault(occupations),
        };
        return new Simulation(_world, new RunSettings { Seed = seed, CropHomelands = homelands }, systems);
    }

    private static FarmingSystem Farming(Simulation sim) => sim.Systems.OfType<FarmingSystem>().Single();

    [Fact]
    public void Real_homelands_put_wild_crops_where_they_grew()
    {
        Simulation sim = FastFarming(1);
        int[] wild = sim.State.IntField(FarmingSystem.WildCropsField);
        CropSet crops = Farming(sim).Crops;
        bool Wild(double lat, double lon, string crop) => (wild[_world.Grid.FindCell(lat, lon)] & (1 << crops.IndexOf(crop))) != 0;

        Assert.True(Wild(36.0, 36.5, "Wheat and barley"), "Wheat should grow wild in the Fertile Crescent.");
        Assert.False(Wild(48.0, 2.0, "Wheat and barley"), "Wheat should not grow wild in France.");
        foreach (Crop crop in crops.Crops)
        {
            int cells = wild.Count(w => (w & (1 << crop.Index)) != 0);
            Assert.True(cells >= 10, $"{crop.Name} grows wild in only {cells} cells.");
        }
        Assert.Equal(crops.Count, Farming(sim).Homelands().Count);
        Assert.Equal(0, wild[_world.Grid.FindCell(23.0, 12.0)]);
    }

    [Fact]
    public void Bad_harvests_cause_famines_that_kill_or_move_farmers_but_never_make_groups_negative()
    {
        Simulation sim = FastFarming(5, harvestSpread: 0.4);
        int[] lastFamine = sim.State.IntField(FarmingSystem.LastFamineField);
        var groups = new PopulationGroups(sim.State);
        int farmer = OccupationSet.LoadDefault().IndexOf(OccupationSet.Farmer);
        var previous = new double[_world.CellCount];
        var now = new double[_world.CellCount];
        int hitCellsThatLostFarmers = 0;
        for (int year = 0; year < 300; year++)
        {
            long yearStart = sim.Tick;
            sim.Run(12);
            for (int group = 0; group < groups.IdCount; group++)
            {
                if (groups.IsAlive(group))
                {
                    Assert.True(groups.Size(group) >= 0.0 && !double.IsNaN(groups.Size(group)));
                }
            }

            groups.FillCellTotals(now, farmer);
            for (int cell = 0; cell < now.Length; cell++)
            {
                if (lastFamine[cell] - 1 >= yearStart && previous[cell] > 0.0 && now[cell] < previous[cell])
                {
                    hitCellsThatLostFarmers++;
                }
            }

            (previous, now) = (now, previous);
        }

        Assert.True(lastFamine.Count(t => t > 0) > 0, "No famine was recorded.");
        Assert.True(hitCellsThatLostFarmers > 0, "No famine-hit cell lost farmers over the year.");
    }

    [Fact]
    public void Famine_shares_must_not_exceed_one_together()
    {
        Assert.Throws<InvalidDataException>(() => FarmingParameters.Parse(
            ContentFiles.ReadText("farming.json").Replace("\"famineDeathShare\": 0.2", "\"famineDeathShare\": 0.8")));
    }

    [Fact]
    public void Farming_starts_in_homelands_turns_foragers_into_farmers_and_spreads()
    {
        Simulation sim = FastFarming(2);
        FarmingSystem farming = Farming(sim);
        int[] wild = sim.State.IntField(FarmingSystem.WildCropsField);
        double foragersBefore = sim.Systems.OfType<ForagerSystem>().Single().WorldPopulation();
        sim.Run(300 * 12);

        var origins = farming.Origins();
        Assert.True(origins.Count >= 4, $"Only {origins.Count} crops were first farmed.");
        Assert.All(origins, o => Assert.True((wild[o.Cell] & (1 << o.Crop)) != 0, "Farming must start where the crop grows wild."));
        Assert.Equal(origins.Count, origins.Select(o => o.Crop).Distinct().Count());

        int[] crop = sim.State.IntField(FarmingSystem.CropField);
        int farmingCells = crop.Count(c => c > 0);
        Assert.True(farmingCells > origins.Count * 5, $"Farming should spread beyond its origins; it covers {farmingCells} cells.");
        Assert.True(farming.WorldFarmers() > 0.0);
        Assert.True(sim.Systems.OfType<ForagerSystem>().Single().WorldPopulation() < foragersBefore);

        var groups = new PopulationGroups(sim.State);
        int farmer = OccupationSet.LoadDefault().IndexOf(OccupationSet.Farmer);
        double[] share = sim.State.DoubleField(FarmingSystem.LandShareField);
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (crop[cell] > 0)
            {
                Assert.True(_world.IsLand(cell));
                Assert.InRange(share[cell], 0.0, 1.0);
                Assert.True(groups.Find(cell, farmer) >= 0, "A farming cell needs a farmer group.");
            }

            Assert.InRange(groups.CountInCell(cell), 0, OccupationSet.LoadDefault().Count);
        }
    }

    [Fact]
    public void Random_homelands_differ_from_real_ones_and_between_seeds()
    {
        List<int> Centres(ulong seed) => Farming(FastFarming(seed, CropHomelands.Random)).Homelands().Select(h => h.Cell).ToList();
        List<int> real = Farming(FastFarming(1)).Homelands().Select(h => h.Cell).ToList();
        List<int> a = Centres(1);
        Assert.NotEqual(real, a);
        Assert.NotEqual(a, Centres(2));
        Assert.Equal(a, Centres(1));
    }

    [Fact]
    public void Settlements_are_founded_in_farming_cells_one_per_cell_and_named()
    {
        Simulation sim = FastFarming(3);
        SettlementSystem settlements = sim.Systems.OfType<SettlementSystem>().Single();
        sim.Run(300 * 12);

        List<SettlementInfo> all = settlements.All();
        Assert.True(all.Count >= 20, $"Only {all.Count} settlements were founded in 300 years.");
        Assert.Equal(all.Count, all.Select(s => s.Cell).Distinct().Count());

        var groups = new PopulationGroups(sim.State);
        int farmer = OccupationSet.LoadDefault().IndexOf(OccupationSet.Farmer);
        int[] index = sim.State.IntField(SettlementSystem.SettlementField);
        foreach (SettlementInfo s in all)
        {
            Assert.Equal(s.Id + 1, index[s.Cell]);
            int group = groups.Find(s.Cell, farmer);
            Assert.True(group >= 0 && groups.Size(group) >= settlements.Parameters.AbandonBelowFarmers,
                $"{s.Name} stands in a cell without farmers.");
            Assert.InRange(s.Population, 0.0, groups.CellTotal(s.Cell));
            Assert.Matches("^[A-Z][a-z]+$", s.Name);
        }

        Assert.True(all.Select(s => s.Name).Distinct().Count() > all.Count / 2, "Names should mostly differ.");
        Assert.Equal(all.Count, index.Count(i => i > 0));
    }

    [Fact]
    public void Settlement_names_are_the_same_for_the_same_seed()
    {
        SettlementParameters p = SettlementParameters.LoadDefault();
        string[] Names(ulong seed)
        {
            var rng = new Rng(seed);
            return Enumerable.Range(0, 20).Select(_ => p.MakeName(rng)).ToArray();
        }

        Assert.Equal(Names(1), Names(1));
        Assert.NotEqual(Names(1), Names(2));
        Assert.All(Names(3), n => Assert.InRange(n.Length, 2, 30));
    }

    [Fact]
    public void Settlement_file_rejects_an_abandon_threshold_above_the_founding_one()
    {
        Assert.Throws<InvalidDataException>(() => SettlementParameters.Parse(
            ContentFiles.ReadText("settlements.json").Replace("\"abandonBelowFarmers\": 100", "\"abandonBelowFarmers\": 5000")));
    }

    [Fact]
    public void Harvests_fill_settlement_stores_and_the_ledger_balances()
    {
        Simulation sim = FastFarming(4);
        var stockpiles = new Stockpiles(sim.State, GoodSet.LoadDefault());
        int grain = stockpiles.Goods.IndexOf(GoodSet.Grain);
        int wild = stockpiles.Goods.IndexOf(GoodSet.WildFood);
        int[] settlement = sim.State.IntField(SettlementSystem.SettlementField);
        for (int year = 0; year < 200; year++)
        {
            sim.Run(12);
            foreach (Good good in stockpiles.Goods.Goods)
            {
                Assert.True(stockpiles.Imbalance(good.Index) < 1e-9, $"{good.Name} ledger is out by {stockpiles.Imbalance(good.Index):E2} in year {year}.");
            }
        }

        double[] stock = stockpiles.StockField(grain);
        Assert.All(stock, v => Assert.True(v >= 0.0 && !double.IsNaN(v)));
        int stocked = 0;
        for (int cell = 0; cell < stock.Length; cell++)
        {
            if (stock[cell] > 0.0)
            {
                stocked++;
                Assert.True(settlement[cell] > 0, "Only settlements hold stock.");
            }
        }

        Assert.True(stocked >= 20, $"Only {stocked} settlements hold grain.");
        Assert.True(stockpiles.Produced(grain) > 0.0 && stockpiles.Consumed(grain) > 0.0 && stockpiles.Spoiled(grain) > 0.0);
        Assert.True(stockpiles.Produced(wild) > 1e6, "Foragers' wild food should be in the ledger.");
        foreach (string gathered in new[] { "Wood", "Clay", "Fibre" })
        {
            int g = stockpiles.Goods.IndexOf(gathered);
            Assert.True(stockpiles.Produced(g) > 0.0, $"No {gathered} was gathered into stores.");
        }

        // Craftsmen make everyday goods, and people use them up.
        foreach (string made in new[] { "Pottery", "Cloth", "Stone tools" })
        {
            int g = stockpiles.Goods.IndexOf(made);
            Assert.True(stockpiles.Produced(g) > 0.0 && stockpiles.Consumed(g) > 0.0, $"No {made} was made and used.");
        }

        var groups = new PopulationGroups(sim.State);
        int craftsman = OccupationSet.LoadDefault().IndexOf(OccupationSet.Craftsman);
        double craftsmen = 0.0;
        double everydayMet = 0.0;
        for (int group = 0; group < groups.IdCount; group++)
        {
            if (groups.IsAlive(group) && groups.Occupation(group) == craftsman)
            {
                craftsmen += groups.Size(group);
                Assert.True(settlement[groups.Cell(group)] > 0 || groups.Size(group) == 0.0, "Craftsmen live in settlements.");
            }

            if (groups.IsAlive(group))
            {
                everydayMet = Math.Max(everydayMet, groups.Everyday(group));
            }
        }

        Assert.True(craftsmen > 0.0, "There should be craftsmen.");

        // Prices stay within their bounds, and gathering stops near what a settlement wants.
        PriceParameters priceParameters = PriceParameters.LoadDefault();
        var prices = new Prices(sim.State, sim.Map, priceParameters, CraftRules.LoadDefault(stockpiles.Goods), OccupationSet.LoadDefault());
        var wanted = new double[stockpiles.Goods.Count];
        var price = new double[stockpiles.Goods.Count];
        int flint = stockpiles.Goods.IndexOf("Flint");
        int clay = stockpiles.Goods.IndexOf("Clay");
        for (int cell = 0; cell < settlement.Length; cell++)
        {
            if (settlement[cell] <= 0)
            {
                continue;
            }

            prices.Work(cell, sim.Tick, wanted, price);
            foreach (Good good in stockpiles.Goods.Goods)
            {
                Assert.InRange(price[good.Index], good.Value * priceParameters.MinFactor * 0.999, good.Value * priceParameters.MaxFactor * 1.001);
            }

            double people = groups.CellTotal(cell);
            Assert.True(stockpiles.Stock(cell, flint) <= wanted[flint] + 0.2 * people + 1.0, "Flint piled up past what was wanted.");
            Assert.True(stockpiles.Stock(cell, clay) <= wanted[clay] + 0.2 * people + 1.0, "Clay piled up past what was wanted.");
        }
        Assert.True(everydayMet > 0.5, "Some group should have most of its everyday needs met.");
    }

    [Fact]
    public void Same_seed_gives_the_same_farming_and_save_load_matches()
    {
        Simulation straight = FastFarming(5);
        straight.Run(120 * 12);

        Simulation first = FastFarming(5);
        first.Run(70 * 12 + 4);
        using var save = new MemoryStream();
        first.Save(save);

        Simulation second = FastFarming(6, CropHomelands.Random);
        second.Load(new MemoryStream(save.ToArray()));
        Assert.Equal(CropHomelands.Real, second.Settings.CropHomelands);
        second.Run(50 * 12 - 4);
        Assert.Equal(straight.StateChecksum(), second.StateChecksum());
    }
}
