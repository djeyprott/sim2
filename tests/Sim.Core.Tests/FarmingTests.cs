using Sim.Core.Climate;
using Sim.Core.Content;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Goods;
using Sim.Core.Herding;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Polities;
using Sim.Core.Sea;
using Sim.Core.Settlements;
using Sim.Core.Technologies;
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
    private Simulation FastFarming(ulong seed, CropHomelands homelands = CropHomelands.Real, double harvestSpread = -1.0, SeafaringParameters? seafaring = null)
    {
        ClimateSystem climate = ClimateSystem.CreateDefault();
        OccupationSet occupations = OccupationSet.LoadDefault();
        FarmingParameters parameters = FarmingParameters.LoadDefault();
        parameters.InventionChance = 0.02;
        if (harvestSpread >= 0.0)
        {
            parameters.HarvestSpread = harvestSpread;
        }
        HerdingParameters herding = HerdingParameters.LoadDefault();
        herding.InventionChance = 0.02;
        var systems = new ISimSystem[]
        {
            climate,
            new PopulationSystem(),
            GoodsSystem.CreateDefault(),
            ForagerSystem.CreateDefault(climate.Biomes, occupations),
            new FarmingSystem(parameters, CropSet.LoadDefault(), climate.Biomes, occupations),
            new HerdingSystem(herding, FastLivestock(), FarmingParameters.LoadDefault(), climate.Biomes, occupations),
            SettlementSystem.CreateDefault(occupations),
            ResourceSystem.CreateDefault(climate.Biomes, occupations),
            CraftSystem.CreateDefault(occupations),
        };
        var trade = TradeSystem.CreateDefault(occupations);
        systems = systems.Append(trade)
            .Append(new TechnologySystem(TechnologyParameters.LoadDefault(), FastTechnologies(occupations), trade, chanceScale: 200.0))
            .Append(new SeafaringSystem(seafaring ?? SeafaringParameters.LoadDefault(), climate.Biomes))
            .Append(PolitySystem.CreateDefault())
            .ToArray();
        return new Simulation(_world, new RunSettings { Seed = seed, CropHomelands = homelands }, systems);
    }

    private static FarmingSystem Farming(Simulation sim) => sim.Systems.OfType<FarmingSystem>().Single();

    // Technologies need farming to have stood only a fiftieth as long as in the real file, so short runs show them.
    private static TechnologySet FastTechnologies(OccupationSet occupations) =>
        TechnologySet.Parse(System.Text.RegularExpressions.Regex.Replace(ContentFiles.ReadText("technologies.json"),
            "\"settledYears\": ([0-9]+)", m => $"\"settledYears\": {int.Parse(m.Groups[1].Value) / 50}"), occupations);

    // Animals wait only a tenth as long after farming nearby as in the real file, so short runs show herding.
    private static LivestockSet FastLivestock() =>
        LivestockSet.Parse(System.Text.RegularExpressions.Regex.Replace(ContentFiles.ReadText("livestock.json"),
            "\"afterFarmingYears\": ([0-9]+)", m => $"\"afterFarmingYears\": {int.Parse(m.Groups[1].Value) / 10}"));

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
    public void Technologies_are_discovered_spread_and_held_by_groups()
    {
        Simulation sim = FastFarming(8);
        TechnologySystem technology = sim.Systems.OfType<TechnologySystem>().Single();
        sim.Run(300 * 12);

        int granaries = technology.Technologies.IndexOf("Granaries");
        var events = technology.Events();
        Assert.Contains(events, e => e.Technology == granaries && e.Kind == TechnologyEventKind.First);
        Assert.All(events.Where(e => e.Kind == TechnologyEventKind.First).GroupBy(e => e.Technology), g => Assert.Single(g));

        // Shares stay between 0 and 1, and granaries are in use by many farmer groups, not just the first.
        var groups = new PopulationGroups(sim.State);
        OccupationSet occupations = OccupationSet.LoadDefault();
        int farmer = occupations.IndexOf(OccupationSet.Farmer);
        int users = 0;
        for (int g = 0; g < groups.IdCount; g++)
        {
            if (!groups.IsAlive(g))
            {
                continue;
            }

            for (int t = 0; t < technology.Technologies.Count; t++)
            {
                Assert.InRange(technology.Knowledge(g, t), 0.0, 1.0);
            }

            if (groups.Occupation(g) == farmer && technology.Knowledge(g, granaries) >= technology.Parameters.InUse)
            {
                users++;
            }
        }

        double most = 0.0;
        int some = 0;
        for (int g = 0; g < groups.IdCount; g++)
        {
            if (groups.IsAlive(g) && technology.Knowledge(g, granaries) > 0.0)
            {
                some++;
                most = Math.Max(most, technology.Knowledge(g, granaries));
            }
        }

        Assert.True(users > 20, $"Only {users} farmer groups use granaries after 300 years; {some} know some, at most {most:F3}; "
            + $"events: {string.Join("; ", events.Take(12).Select(e => $"{e.Technology} {e.Kind} year {e.Tick / 12} group {e.Group:F0}"))}.");

        // Discoveries happen only where a technology's conditions are met.
        foreach (TechnologyEvent e in events.Where(e => e.Kind != TechnologyEventKind.Lost))
        {
            Technology t = technology.Technologies.Get(e.Technology);
            Assert.True(!t.River || _world.River(e.Cell) != RiverSize.None, $"{t.Name} discovered away from a river.");
            Assert.True(t.Terrain.Count == 0 || t.Terrain.Contains(_world.Terrain(e.Cell)), $"{t.Name} discovered on the wrong terrain.");
        }
    }

    [Fact]
    public void Technologies_change_farm_yields_spoilage_and_herds_where_used()
    {
        // Two copies of the same world: in one every group knows the plough, granaries, and milk and
        // wool; in the other nobody knows any technology that changes farming or herding.
        Simulation with = FastFarming(8);
        with.Run(300 * 12);
        var save = new MemoryStream();
        with.Save(save);
        Simulation without = FastFarming(8);
        without.Load(new MemoryStream(save.ToArray()));

        TechnologySystem technology = with.Systems.OfType<TechnologySystem>().Single();
        TechnologySet set = technology.Technologies;
        int plough = set.IndexOf("The plough");
        int granaries = set.IndexOf("Granaries");
        int milk = set.IndexOf("Milk and wool");
        int wheel = set.IndexOf("The wheel");
        void Teach(Simulation sim, bool known)
        {
            EntityTable table = sim.State.Table(PopulationGroups.TableName);
            foreach (Technology t in set.Technologies)
            {
                bool taught = known && (t.Index == plough || t.Index == granaries || t.Index == milk || t.Index == wheel);
                Span<double> shares = table.DoubleColumn(PopulationGroups.KnowledgeColumnPrefix + t.Slug).Values;
                for (int g = 0; g < shares.Length; g++)
                {
                    shares[g] = taught ? 1.0 : 0.0;
                }
            }
        }

        Teach(with, true);
        Teach(without, false);

        // Cattle have barely spread in so short a run: every herding cell, every wheat and barley cell
        // (where the plough can be used) and every other farming cell get them, in both copies (so
        // some farmland has cattle and some has none).
        int wheatAndBarley = Farming(with).Crops.IndexOf("Wheat and barley") + 1;
        int cattleBit = 1 << LivestockSet.LoadDefault().IndexOf("Cattle");
        var keepers = new PopulationGroups(with.State);
        int[] cropOf = with.State.IntField(FarmingSystem.CropField);
        foreach (Simulation sim in new[] { with, without })
        {
            int[] kept = sim.State.IntField(HerdingSystem.AnimalsField);
            for (int cell = 0; cell < _world.CellCount; cell++)
            {
                if ((cropOf[cell] > 0 && (cell % 2 == 0 || cropOf[cell] == wheatAndBarley)) || keepers.Find(cell, OccupationSet.LoadDefault().IndexOf(OccupationSet.Herder)) >= 0)
                {
                    kept[cell] |= cattleBit;
                }
            }
        }

        int[] animalsBefore = (int[])with.State.IntField(HerdingSystem.AnimalsField).Clone();
        var groups = new PopulationGroups(with.State);
        OccupationSet occupations = OccupationSet.LoadDefault();
        int herder = occupations.IndexOf(OccupationSet.Herder);
        int farmer = occupations.IndexOf(OccupationSet.Farmer);
        bool[] hadHerders = Enumerable.Range(0, _world.CellCount).Select(c => groups.Find(c, herder) >= 0).ToArray();

        // Herding runs in January, before the technology system; each hemisphere's harvest follows.
        with.Run(10);
        without.Run(10);

        // Milk and wool: herders on pasture with sheep, goats or cattle feed 1.4 times as many.
        HerdingSystem herding = with.Systems.OfType<HerdingSystem>().Single();
        int milkAnimals = (1 << herding.Livestock.IndexOf("Sheep and goats")) | (1 << herding.Livestock.IndexOf("Cattle"));
        double[] herdWith = with.State.DoubleField(HerdingSystem.CapacityField);
        double[] herdWithout = without.State.DoubleField(HerdingSystem.CapacityField);
        int milkCells = 0;
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (herdWithout[cell] <= 0.0 || !hadHerders[cell] || (animalsBefore[cell] & milkAnimals) == 0)
            {
                continue;
            }

            milkCells++;
            Assert.Equal(1.4, herdWith[cell] / herdWithout[cell], 6);
        }

        // The plough: wheat and barley farmland with cattle feeds 1.25 times as many, and open
        // grassland away from rivers twice that again (its penalty lifted); elsewhere nothing changes.
        double[] farmWith = with.State.DoubleField(FarmingSystem.CapacityField);
        double[] farmWithout = without.State.DoubleField(FarmingSystem.CapacityField);
        int[] cropWith = with.State.IntField(FarmingSystem.CropField);
        int[] cropWithout = without.State.IntField(FarmingSystem.CropField);
        int[] sinceWith = with.State.IntField(FarmingSystem.SinceField);
        int[] animalsWith = with.State.IntField(HerdingSystem.AnimalsField);
        int[] animalsWithout = without.State.IntField(HerdingSystem.AnimalsField);
        int[] biome = with.State.IntField(ClimateSystem.BiomeField);
        int grassland = with.Systems.OfType<ClimateSystem>().Single().Biomes.IndexOf("Grassland");
        int ploughed = 0, ploughedGrassland = 0, unploughed = 0;
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (farmWithout[cell] <= 0.0 || farmWith[cell] <= 0.0 || cropWith[cell] != cropWithout[cell]
                || sinceWith[cell] - 1 >= 300 * 12 || animalsWith[cell] != animalsWithout[cell])
            {
                continue;
            }

            int group = groups.Find(cell, farmer);
            if (group < 0 || technology.Knowledge(group, plough) < 0.5)
            {
                continue;
            }

            double ratio = farmWith[cell] / farmWithout[cell];
            if (!technology.CellAllows(cell, plough))
            {
                unploughed++;
                Assert.Equal(1.0, ratio, 6);
                continue;
            }

            bool open = biome[cell] == grassland && _world.River(cell) == RiverSize.None;
            Assert.Equal(open ? 2.5 : 1.25, ratio, 6);
            ploughed++;
            ploughedGrassland += open ? 1 : 0;
        }

        // Granaries cut the grain that spoils in store where the farmers use them (to 0.8).
        FarmingSystem farmingWith = Farming(with);
        FarmingSystem farmingWithout = Farming(without);
        double grainSpoilage = GoodSet.LoadDefault().Goods.Single(g => g.Name == GoodSet.Grain).Spoilage;
        int[] settlement = with.State.IntField(SettlementSystem.SettlementField);
        int stores = 0;
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            int group = groups.Find(cell, farmer);
            if (settlement[cell] > 0 && group >= 0 && technology.Knowledge(group, granaries) >= 0.5)
            {
                stores++;
                Assert.Equal(grainSpoilage * 0.8, farmingWith.GrainSpoilage(cell), 9);
                Assert.Equal(grainSpoilage, farmingWithout.GrainSpoilage(cell), 9);
            }
        }

        // The wheel makes land carrying cheaper for settlements whose farmers keep cattle (0.7 of the cost).
        TechnologyUse use = TechnologyUse.TryCreate(with.State, _world)!;
        TechnologyEffect[] carrying = use.WithEffect(TechnologySet.LandCarrying);
        int carts = 0;
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            int farmers = groups.Find(cell, farmer);
            if (settlement[cell] > 0 && farmers >= 0 && groups.Size(farmers) > 0.0)
            {
                bool cattle = technology.CellAllows(cell, wheel);
                carts += cattle ? 1 : 0;
                Assert.Equal(cattle ? 0.7 : 1.0, use.CellFactor(cell, carrying), 9);
            }
        }

        Assert.True(milkCells >= 3 && ploughed >= 5 && unploughed >= 5 && stores >= 5 && carts >= 1,
            $"Too few cells to compare: {milkCells} with milk, {ploughed} ploughed ({ploughedGrassland} open grassland), {unploughed} without cattle or wheat and barley, {stores} stores, {carts} settlements with carts.");
    }

    [Fact]
    public void Copper_is_worked_only_where_known_and_metal_tools_stand_in_for_stone()
    {
        Simulation with = FastFarming(8);
        with.Run(300 * 12);
        var save = new MemoryStream();
        with.Save(save);
        Simulation without = FastFarming(8);
        without.Load(new MemoryStream(save.ToArray()));

        TechnologySet set = with.Systems.OfType<TechnologySystem>().Single().Technologies;
        int copperWorking = set.IndexOf("Copper working");
        int bronze = set.IndexOf("Bronze");
        void Teach(Simulation sim, double share)
        {
            EntityTable table = sim.State.Table(PopulationGroups.TableName);
            table.DoubleColumn(PopulationGroups.KnowledgeColumnPrefix + set.Get(copperWorking).Slug).Values.Fill(share);
            table.DoubleColumn(PopulationGroups.KnowledgeColumnPrefix + set.Get(bronze).Slug).Values.Fill(0.0);
        }

        Teach(with, 1.0);
        Teach(without, 0.0);
        GoodSet goods = GoodSet.LoadDefault();
        int copper = goods.IndexOf("Copper");
        int copperTools = goods.IndexOf("Copper tools");
        int bronzeTools = goods.IndexOf("Bronze tools");
        int stoneTools = goods.IndexOf("Stone tools");
        var stockWith = new Stockpiles(with.State, goods);
        var stockWithout = new Stockpiles(without.State, goods);
        // No settlement stands by copper ore yet in this short run, so every settlement is given the
        // same ore in both copies (as if traded in).
        int ore = goods.IndexOf("Copper ore");
        int[] settled = with.State.IntField(SettlementSystem.SettlementField);
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (settled[cell] > 0)
            {
                stockWith.Produce(cell, ore, 20.0);
                stockWithout.Produce(cell, ore, 20.0);
            }
        }

        double copperBefore = stockWith.Produced(copper), toolsBefore = stockWith.Produced(copperTools);
        Assert.Equal(toolsBefore, stockWithout.Produced(copperTools));

        with.Run(36);
        without.Run(36);

        // Where everyone knows copper working, ore near the sources is smelted and made into tools;
        // where nobody does, no copper or copper tools are made at all.
        string Diagnosis()
        {
            var near = Enumerable.Range(0, _world.CellCount).Where(c => settled[c] > 0 && stockWith.Stock(c, ore) > 0.0).ToList();
            CraftRules craft = CraftRules.LoadDefault(goods);
            var prices = new Prices(with.State, _world, PriceParameters.LoadDefault(), craft, OccupationSet.LoadDefault());
            var wanted = new double[goods.Count];
            var priceOf = new double[goods.Count];
            string sample = "";
            if (near.Count > 0)
            {
                int cell = near[0];
                prices.Work(cell, with.Tick, wanted, priceOf);
                Recipe smelt = craft.Recipes.First(r => r.Output == copper);
                sample = $"cell {cell}: fed {prices.StoreFed(cell):F0}, smelting allowed {prices.Allowed(smelt)}, "
                    + string.Join(", ", new[] { "Copper ore", "Wood", "Copper", "Copper tools", "Stone tools", "Grain" }
                        .Select(n => $"{n} wanted {wanted[goods.IndexOf(n)]:F1} stock {stockWith.Stock(cell, goods.IndexOf(n)):F1} price {priceOf[goods.IndexOf(n)]:F2}"));
            }

            return $"{near.Count} settlements holding ore; ore gathered {stockWith.Produced(ore):F1}, in store {stockWith.InStore(ore):F1}; {sample}";
        }

        Assert.True(stockWith.Produced(copper) > copperBefore && stockWith.Produced(copperTools) > toolsBefore,
            $"Copper made {stockWith.Produced(copper) - copperBefore:F1}, copper tools {stockWith.Produced(copperTools) - toolsBefore:F1} in 3 years. {Diagnosis()}");
        Assert.Equal(copperBefore, stockWithout.Produced(copper));
        Assert.Equal(toolsBefore, stockWithout.Produced(copperTools));

        // Wants follow the better tools in store: bronze tools for the whole need, copper tools for
        // what bronze does not cover, and stone for the rest; and copper only where it is known.
        OccupationSet occupations = OccupationSet.LoadDefault();
        CraftRules rules = CraftRules.LoadDefault(goods);
        PriceParameters parameters = PriceParameters.LoadDefault();
        int[] settlement = with.State.IntField(SettlementSystem.SettlementField);
        foreach ((Simulation sim, Stockpiles stock, bool known) in new[] { (with, stockWith, true), (without, stockWithout, false) })
        {
            var prices = new Prices(sim.State, _world, parameters, rules, occupations);
            var wanted = new double[goods.Count];
            int checkedCells = 0;
            for (int cell = 0; cell < _world.CellCount && checkedCells < 50; cell++)
            {
                if (settlement[cell] <= 0 || prices.StoreFed(cell) <= 0.0)
                {
                    continue;
                }

                checkedCells++;
                prices.Work(cell, sim.Tick, wanted);
                double need = rules.NeedPerPerson[stoneTools] * prices.StoreFed(cell) * (1.0 + rules.ReserveYears);
                double Sales(int good) => parameters.SalesWanted * stock.Sold(cell, good);
                Assert.Equal(need / 5.0 + Sales(bronzeTools), wanted[bronzeTools], 6);
                double afterBronze = Math.Max(0.0, need - 5.0 * stock.Stock(cell, bronzeTools));
                Assert.Equal(afterBronze / 3.0 + Sales(copperTools), wanted[copperTools], 6);
                Assert.Equal(Math.Max(0.0, afterBronze - 3.0 * stock.Stock(cell, copperTools)) + Sales(stoneTools), wanted[stoneTools], 6);
                if (!known)
                {
                    Assert.Equal(Sales(copper), wanted[copper], 9);
                }
            }

            Assert.True(checkedCells > 0);
        }
    }

    [Fact]
    public void Copper_working_is_kept_only_where_ore_or_copper_is_within_reach()
    {
        Simulation sim = FastFarming(8);
        sim.Run(300 * 12);
        TechnologySystem technology = sim.Systems.OfType<TechnologySystem>().Single();
        TechnologySet set = technology.Technologies;
        int copperWorking = set.IndexOf("Copper working");
        DoubleColumn known = sim.State.Table(PopulationGroups.TableName)
            .DoubleColumn(PopulationGroups.KnowledgeColumnPrefix + set.Get(copperWorking).Slug);
        var groups = new PopulationGroups(sim.State);
        int farmer = OccupationSet.LoadDefault().IndexOf(OccupationSet.Farmer);
        int craftsman = OccupationSet.LoadDefault().IndexOf(OccupationSet.Craftsman);

        // Farmers and craftsmen know it (foragers and herders cannot use it, so they would keep it
        // and bring it back whenever they took up farming).
        for (int g = 0; g < groups.IdCount; g++)
        {
            known[g] = groups.IsAlive(g) && (groups.Occupation(g) == farmer || groups.Occupation(g) == craftsman) ? 1.0 : 0.0;
        }

        // Every fourth settlement holds plenty of copper (as if smelted there); the rest none.
        GoodSet goods = GoodSet.LoadDefault();
        var stock = new Stockpiles(sim.State, goods);
        int copper = goods.IndexOf("Copper");
        int[] settled = sim.State.IntField(SettlementSystem.SettlementField);
        int[] settlementCells = Enumerable.Range(0, _world.CellCount).Where(c => settled[c] > 0).ToArray();
        for (int i = 0; i < settlementCells.Length; i += 4)
        {
            stock.Produce(settlementCells[i], copper, 1e5);
        }

        sim.Run(60 * 12);

        // Farmers who still use it live where copper or ore is near: held or gathered in their own
        // cell or by a trade partner; elsewhere they have mostly forgotten it.
        TechnologyConditions conditions = TechnologyUse.TryCreate(sim.State, _world)!.Conditions;
        int keptAllowed = 0, keptElsewhere = 0, byPartner = 0, unsupplied = 0;
        double unsuppliedShare = 0.0;
        for (int g = 0; g < groups.IdCount; g++)
        {
            if (!groups.IsAlive(g) || groups.Occupation(g) != farmer || groups.Size(g) <= 0.0)
            {
                continue;
            }

            int cell = groups.Cell(g);
            bool allows = technology.CellAllows(cell, copperWorking);
            if (known[g] >= technology.Parameters.InUse)
            {
                keptAllowed += allows ? 1 : 0;
                keptElsewhere += allows ? 0 : 1;
                byPartner += allows && !conditions.SuppliedHere(cell, copperWorking) ? 1 : 0;
            }

            if (!allows)
            {
                unsupplied++;
                unsuppliedShare += known[g];
            }
        }

        Assert.True(keptAllowed > 0 && unsupplied > 0,
            $"{keptAllowed} farmer groups use it where copper is near ({byPartner} through a trade partner), {unsupplied} groups have none near.");
        // Copper comes and goes as it is traded and used, so a few groups lost it lately and have
        // not yet forgotten (a share of 1 falls below a half in about 20 years).
        Assert.True(keptElsewhere <= 0.2 * (keptAllowed + keptElsewhere), $"{keptElsewhere} groups with no copper near still use it, against {keptAllowed} with.");
        // Farmers moving out of cells with copper (to new land, from famine) keep bringing some of it.
        Assert.True(unsuppliedShare / unsupplied < 0.5, $"Groups with no copper near still know {unsuppliedShare / unsupplied:F2} of it on average.");
    }

    [Fact]
    public void Cattle_are_not_kept_in_tropical_forest_and_the_plough_needs_wheat_and_barley()
    {
        Simulation sim = FastFarming(4);
        sim.Run(300 * 12);
        HerdingSystem herding = sim.Systems.OfType<HerdingSystem>().Single();
        TechnologySystem technology = sim.Systems.OfType<TechnologySystem>().Single();
        BiomeSet biomes = sim.Systems.OfType<ClimateSystem>().Single().Biomes;
        int[] biome = sim.State.IntField(ClimateSystem.BiomeField);
        int cattle = herding.Livestock.IndexOf("Cattle");
        int[] forests = { biomes.IndexOf("Rainforest"), biomes.IndexOf("Tropical seasonal forest") };
        int forestCells = 0;
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (_world.IsLand(cell) && forests.Contains(biome[cell]))
            {
                forestCells++;
                Assert.Equal(0.0, herding.Suitability(cell, cattle));
            }
        }

        Assert.True(forestCells > 1000);
        Assert.Contains(Enumerable.Range(0, _world.CellCount), c => _world.IsLand(c) && biome[c] == biomes.IndexOf("Savanna") && herding.Suitability(c, cattle) > 0.5);

        // Give every farming cell cattle: the plough is allowed only where wheat and barley are farmed.
        int plough = technology.Technologies.IndexOf("The plough");
        int[] crop = sim.State.IntField(FarmingSystem.CropField);
        int[] kept = sim.State.IntField(HerdingSystem.AnimalsField);
        int wheat = Farming(sim).Crops.IndexOf("Wheat and barley") + 1;
        int wheatCells = 0, otherCells = 0;
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (crop[cell] > 0)
            {
                kept[cell] |= 1 << cattle;
                bool allows = technology.CellAllows(cell, plough);
                Assert.Equal(crop[cell] == wheat, allows);
                wheatCells += allows ? 1 : 0;
                otherCells += allows ? 0 : 1;
            }
        }

        Assert.True(wheatCells > 0 && otherCells > 0, $"{wheatCells} wheat and barley cells, {otherCells} others.");
    }

    [Fact]
    public void A_polity_founded_at_a_settlement_rules_its_cells_and_is_saved()
    {
        Simulation sim = FastFarming(6);
        sim.Run(300 * 12);
        PolitySystem polities = sim.Systems.OfType<PolitySystem>().Single();
        SettlementSystem settlements = sim.Systems.OfType<SettlementSystem>().Single();
        Assert.Empty(polities.All());
        int capital = settlements.All().OrderBy(s => s.Id).First().Cell;
        int id = polities.Found(capital, sim.Tick, new Rng(11));

        // Named after its capital, a chiefdom, with a temperament and empty positions.
        PolityInfo info = polities.Info(id);
        Assert.Equal(settlements.InCell(capital)!.Value.Name, info.Name);
        Assert.Equal(PolityLevel.Chiefdom, info.Level);
        Assert.True(info.Standing);
        PolityParameters parameters = PolityParameters.LoadDefault();
        foreach (double t in new[] { info.Aggression, info.Greed, info.Expansion })
        {
            Assert.InRange(t, parameters.MinTemperament, parameters.MaxTemperament);
        }

        Assert.Equal(parameters.Positions, polities.Positions(id).Select(p => p.Position));
        Assert.All(polities.Positions(id), p => Assert.Equal(-1, p.Holder));

        // It rules its capital and the land around it, and nothing else.
        var ruled = Enumerable.Range(0, _world.CellCount).Where(c => polities.RulerOf(c) == id).ToHashSet();
        Assert.Contains(capital, ruled);
        var around = new List<int>();
        foreach (int c in _world.Grid.Neighbours(capital))
        {
            around.Add(c);
        }

        Assert.All(around.Where(_world.IsLand), c => Assert.Contains(c, ruled));
        Assert.All(ruled, c => Assert.True(c == capital || around.Contains(c)));
        Assert.Throws<ArgumentException>(() => polities.Found(capital, sim.Tick, new Rng(12)));

        // Saved and loaded with the rest of the world.
        polities.SetLevel(id, PolityLevel.State);
        var save = new MemoryStream();
        sim.Save(save);
        Simulation loaded = FastFarming(6);
        loaded.Load(new MemoryStream(save.ToArray()));
        Assert.Equal(sim.StateChecksum(), loaded.StateChecksum());
        PolitySystem loadedPolities = loaded.Systems.OfType<PolitySystem>().Single();
        Assert.Equal(polities.Info(id), loadedPolities.Info(id));
        Assert.Equal(ruled.Count, Enumerable.Range(0, _world.CellCount).Count(c => loadedPolities.RulerOf(c) == id));

        // An ended polity rules nothing but stays on record.
        polities.End(id, sim.Tick);
        Assert.DoesNotContain(Enumerable.Range(0, _world.CellCount), c => polities.RulerOf(c) >= 0);
        Assert.Single(polities.All());
        Assert.Empty(polities.Standing());
        Assert.False(polities.Info(id).Standing);
    }

    [Fact]
    public void Horses_are_kept_where_people_know_horse_herding_and_horses_live_wild()
    {
        Simulation sim = FastFarming(7);
        sim.Run(300 * 12);
        HerdingSystem herding = sim.Systems.OfType<HerdingSystem>().Single();
        TechnologySet set = sim.Systems.OfType<TechnologySystem>().Single().Technologies;
        int horses = herding.Livestock.IndexOf("Horses");
        int horseHomeland = herding.Livestock.Get(horses).FirstHomeland;
        int[] wild = sim.State.IntField(HerdingSystem.WildField);
        int[] animals = sim.State.IntField(HerdingSystem.AnimalsField);
        Assert.DoesNotContain(herding.Origins(), o => o.Animal == horses);
        Assert.DoesNotContain(Enumerable.Range(0, _world.CellCount), c => (animals[c] & (1 << horses)) != 0);

        // Herders who know horse herding move into the wild horses' best cell, and one far outside.
        int steppe = Enumerable.Range(0, _world.CellCount).Where(c => (wild[c] & (1 << horseHomeland)) != 0)
            .OrderByDescending(c => herding.Suitability(c, horses)).ThenBy(c => c).First();
        int outside = Enumerable.Range(0, _world.CellCount)
            .Where(c => _world.IsLand(c) && wild[c] == 0 && herding.Suitability(c, horses) > 0.5 && _world.Grid.DistanceKm(c, steppe) > 2000.0)
            .OrderBy(c => c).First();
        var groups = new PopulationGroups(sim.State);
        int herder = OccupationSet.LoadDefault().IndexOf(OccupationSet.Herder);
        DoubleColumn knowledge = sim.State.Table(PopulationGroups.TableName)
            .DoubleColumn(PopulationGroups.KnowledgeColumnPrefix + set.Get(set.IndexOf("Horse herding")).Slug);
        foreach (int cell in new[] { steppe, outside })
        {
            int group = groups.FindOrAdd(cell, herder);
            groups.AddPeople(group, 2000.0, 1.0);
            knowledge[group] = 1.0;
        }

        sim.Run(12);
        Assert.True((animals[steppe] & (1 << horses)) != 0, "Horses are not kept where they live wild and herders know horse herding.");
        Assert.True((animals[outside] & (1 << horses)) == 0, "Horses are kept where they never lived wild.");
        Assert.Contains(herding.Origins(), o => o.Animal == horses && o.Cell == steppe);
    }

    [Fact]
    public void Unreached_land_is_settled_only_by_boats_that_reach_it()
    {
        // Settlers set out whenever boats reach, so one year shows what happens.
        var parameters = SeafaringParameters.LoadDefault();
        parameters.SettleChance = 1.0;
        Simulation sim = FastFarming(3, seafaring: parameters);
        SeafaringSystem seafaring = sim.Systems.OfType<SeafaringSystem>().Single();
        ForagerSystem foragers = sim.Systems.OfType<ForagerSystem>().Single();
        TechnologySet set = sim.Systems.OfType<TechnologySystem>().Single().Technologies;
        SeaMap sea = SeaMap.Of(_world);
        Assert.Contains(seafaring.Targets(), t => t.Landmass == sea.Landmass[_world.Grid.FindCell(-19, 47)]);
        Assert.False(foragers.IsReachable(_world.Grid.FindCell(-19, 47)), "Madagascar was reached at the start.");

        // Clear any boats people found by themselves, then put sailors (range 8) beside an island
        // whose nearest shore is farther, and canoe people (range 50) beside one that is nearer.
        var groups = new PopulationGroups(sim.State);
        EntityTable table = sim.State.Table(PopulationGroups.TableName);
        foreach (Technology t in set.Technologies)
        {
            table.DoubleColumn(PopulationGroups.KnowledgeColumnPrefix + t.Slug).Values.Fill(0.0);
        }

        // Starting points on reached land, landing where people can live.
        int[] biome = sim.State.IntField(ClimateSystem.BiomeField);
        BiomeSet biomes = sim.Systems.OfType<ClimateSystem>().Single().Biomes;
        ForagerParameters foragerFile = ForagerParameters.LoadDefault();
        bool Usable((int From, int To, int Steps) start) => seafaring.Reached(sea.Landmass[start.From])
            && biome[start.To] >= 0 && foragerFile.Density[biomes.Get(biome[start.To]).Name] > 0.0;
        var targets = seafaring.Targets().Where(t => !seafaring.Reached(t.Landmass))
            .Select(t => (t.Landmass, Starts: t.Starts.Where(Usable).ToArray())).Where(t => t.Starts.Length > 0).ToList();
        var far = targets.First(t => t.Starts[0].Steps > 8 && t.Starts[0].Steps <= 50);
        var near = targets.First(t => t.Landmass != far.Landmass && t.Starts[0].Steps <= 50
            && !far.Starts.Any(s => s.From == t.Starts[0].From) && !t.Starts.Any(s => s.From == far.Starts[0].From && s.Steps <= 8));
        int farmer = OccupationSet.LoadDefault().IndexOf(OccupationSet.Farmer);
        void Sailors(int cell, string boats)
        {
            int group = groups.FindOrAdd(cell, farmer);
            groups.AddPeople(group, 5000.0, 1.0);
            table.DoubleColumn(PopulationGroups.KnowledgeColumnPrefix + set.Get(set.IndexOf(boats)).Slug)[group] = 1.0;
            table.DoubleColumn(PopulationGroups.KnowledgeColumnPrefix + set.Get(set.IndexOf("The sail")).Slug)[group] = 1.0;
        }

        Sailors(far.Starts[0].From, "The sail");
        Sailors(near.Starts[0].From, "Outrigger canoes");
        int before = seafaring.Landings().Count;
        sim.Run(12);
        var landings = seafaring.Landings().Skip(before).ToList();
        Assert.Contains(landings, l => l.Landmass == near.Landmass && l.From == near.Starts[0].From);
        Assert.DoesNotContain(landings, l => l.Landmass == far.Landmass);
        Assert.True(seafaring.Reached(near.Landmass) && !seafaring.Reached(far.Landmass));

        // The next year the foragers count the new land as theirs and live on it.
        sim.Run(12);
        Landing landing = landings.First(l => l.Landmass == near.Landmass);
        Assert.True(foragers.IsReachable(landing.Cell));
        Assert.True(groups.CellTotal(landing.Cell) > 0.0, "Nobody lives where the settlers landed.");
    }

    [Fact]
    public void Sea_crossings_follow_the_sea()
    {
        SeaMap sea = SeaMap.Of(_world);
        Crossings crossings = sea.CrossingsWithin(10);
        int ShoreNear(double lat, double lon)
        {
            int cell = _world.Grid.FindCell(lat, lon);
            return sea.ShoreCells.OrderBy(c => _world.Grid.DistanceKm(c, cell)).ThenBy(c => c).First();
        }

        int Steps(double latA, double lonA, double latB, double lonB)
        {
            int a = ShoreNear(latA, lonA), b = ShoreNear(latB, lonB);
            int best = int.MaxValue;
            for (int k = crossings.Start[a]; k < crossings.Start[a + 1]; k++)
            {
                if (crossings.Target[k] == b)
                {
                    best = Math.Min(best, crossings.Steps[k]);
                }
            }

            return best;
        }

        // Dover and Calais are a short crossing apart; Dover and New York are not within 10 sea cells.
        Assert.InRange(Steps(51.13, 1.31, 50.95, 1.85), 1, 2);
        Assert.Equal(int.MaxValue, Steps(51.13, 1.31, 40.7, -74.0));
        Assert.NotEqual(sea.Landmass[_world.Grid.FindCell(52, 0)], sea.Landmass[_world.Grid.FindCell(48, 2)]);
        Assert.Equal(-1, sea.Landmass[_world.Grid.FindCell(45, -30)]);
    }

    [Fact]
    public void Herding_is_taken_up_in_its_homelands_and_spreads()
    {
        Simulation sim = FastFarming(7);
        HerdingSystem herding = sim.Systems.OfType<HerdingSystem>().Single();
        sim.Run(300 * 12);

        // Animals are first herded inside their homelands, once per homeland at most.
        var origins = herding.Origins();
        Assert.True(origins.Count >= 3, $"Only {origins.Count} first herdings in 300 years.");
        Assert.Equal(origins.Count, origins.Select(o => o.Homeland).Distinct().Count());
        long firstFarming = Farming(sim).Origins().Min(o => o.Tick);
        foreach ((int animal, int homeland, int cell, long tick) in origins)
        {
            AnimalHomeland h = herding.Livestock.Homelands[homeland];
            Assert.Equal(h.Animal, animal);
            Assert.True(_world.Grid.DistanceKm(cell, _world.Grid.FindCell(h.Lat, h.Lon)) <= h.RadiusKm + 60.0,
                $"{herding.Livestock.Get(animal).Name} was first herded outside its homeland.");

            // Outside forager homelands, only where farming has stood for the wait.
            Assert.True(h.Foragers || tick >= firstFarming + (long)(h.AfterFarmingYears * 12),
                $"{herding.Livestock.Get(animal).Name} was first herded in month {tick}, before farming had stood {h.AfterFarmingYears} years (first farming in month {firstFarming}).");
            Assert.Equal("", herding.Livestock.Get(animal).TamedBy);
        }

        // Herders live and grow on pasture, and animals reach farmers beyond where they were first kept.
        var groups = new PopulationGroups(sim.State);
        int herder = OccupationSet.LoadDefault().IndexOf(OccupationSet.Herder);
        Assert.True(herding.WorldHerders() > 10_000, $"Only {herding.WorldHerders():F0} herders after 300 years.");
        int[] animals = sim.State.IntField(HerdingSystem.AnimalsField);
        int keptCells = animals.Count(a => a != 0);
        Assert.True(keptCells > 3 * origins.Count, $"Animals are kept in only {keptCells} cells.");
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            int group = groups.Find(cell, herder);
            if (group >= 0 && groups.Size(group) > 0.0)
            {
                Assert.True(animals[cell] != 0, $"Cell {cell} has herders but no animals.");
                Assert.True(herding.Pasture(cell) > 0.0, $"Cell {cell} has herders but no pasture.");
            }
        }

        // Wool and hides reach settlement stores, and the ledger still balances.
        var stockpiles = new Stockpiles(sim.State, GoodSet.LoadDefault());
        Assert.True(stockpiles.Produced(stockpiles.Goods.IndexOf("Hides")) > 0.0, "No hides were produced.");
        Assert.True(stockpiles.Produced(stockpiles.Goods.IndexOf("Meat and milk")) > 0.0, "Herders ate nothing.");
        foreach (Good good in stockpiles.Goods.Goods)
        {
            Assert.True(stockpiles.Imbalance(good.Index) < 1e-6, $"{good.Name} ledger is out by {stockpiles.Imbalance(good.Index):E2}.");
        }
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

        // Settlements are linked and trade with each other.
        TradeSystem trade = sim.Systems.OfType<TradeSystem>().Single();
        Assert.True(trade.LinkCount > 10, $"Only {trade.LinkCount} trade links.");
        Assert.True(trade.Routes().Count > 0, "No trade routes.");
        Assert.True(Enumerable.Range(0, stockpiles.Goods.Count).Any(g => trade.Routes(g).Count > 0), "No good has routes of its own.");
        Assert.True(trade.Routes(stockpiles.Goods.IndexOf(GoodSet.Grain)).All(r => r.Value > 0.0));
        Assert.True(Enumerable.Range(0, stockpiles.Goods.Count).Any(g => stockpiles.InTransit(g) > 0.0), "Nothing is in transit.");

        // Prices stay within their bounds, and gathering stops by the stock at which prices bottom out.
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
                Assert.InRange(price[good.Index], good.Value * priceParameters.MinFactor * 0.999, good.Value * Prices.MaxFactorOf(good, priceParameters) * 1.001);
            }

            double people = groups.CellTotal(cell);
            double most = 1.0 / priceParameters.MinFactor;
            Assert.True(stockpiles.Stock(cell, flint) <= most * wanted[flint] + 0.2 * people + 1.0, "Flint piled up past where its price bottoms out.");
            Assert.True(stockpiles.Stock(cell, clay) <= most * wanted[clay] + 0.2 * people + 1.0, "Clay piled up past where its price bottoms out.");
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
