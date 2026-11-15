using Sim.Core.Climate;
using Sim.Core.Content;
using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Map;
using Sim.Core.People;
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
            ForagerSystem.CreateDefault(climate.Biomes, occupations),
            new FarmingSystem(parameters, CropSet.LoadDefault(), climate.Biomes, occupations),
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
        double previous = 0.0;
        bool dropped = false;
        for (int year = 0; year < 300; year++)
        {
            sim.Run(12);
            for (int group = 0; group < groups.IdCount; group++)
            {
                if (groups.IsAlive(group))
                {
                    Assert.True(groups.Size(group) >= 0.0 && !double.IsNaN(groups.Size(group)));
                }
            }

            double farmers = Farming(sim).WorldFarmers();
            if (year > 100 && farmers < previous * 0.98)
            {
                dropped = true;
            }

            previous = farmers;
        }

        Assert.True(lastFamine.Count(t => t > 0) > 0, "No famine was recorded.");
        Assert.True(dropped, "World farmers never fell from one year to the next.");
    }

    [Fact]
    public void Famine_shares_must_not_exceed_one_together()
    {
        Assert.Throws<InvalidDataException>(() => FarmingParameters.Parse(
            ContentFiles.ReadText("farming.json").Replace("\"famineDeathShare\": 0.5", "\"famineDeathShare\": 0.8")));
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

            Assert.InRange(groups.CountInCell(cell), 0, 2);
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
