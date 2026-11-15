using Sim.Core.Climate;
using Sim.Core.Engine;
using Sim.Core.Map;
using Sim.Core.People;
using Xunit;

namespace Sim.Core.Tests;

public class ForagerTests : IClassFixture<EarthWorldFixture>
{
    private readonly WorldMap _world;

    public ForagerTests(EarthWorldFixture fixture)
    {
        _world = fixture.World;
    }

    private Simulation NewSimulation(ulong seed = 1) =>
        new Simulation(_world, new RunSettings { Seed = seed }, SystemCatalog.CreateDefault());

    private static ForagerSystem Foragers(Simulation sim) => sim.Systems.OfType<ForagerSystem>().Single();

    private bool Reached(Simulation sim, double lat, double lon) => Foragers(sim).IsReachable(_world.Grid.FindCell(lat, lon));

    private double[] PeoplePerCell(Simulation sim)
    {
        var totals = new double[_world.CellCount];
        new PopulationGroups(sim.State).FillCellTotals(totals);
        return totals;
    }

    [Fact]
    public void The_right_land_is_reached_at_the_start()
    {
        Simulation sim = NewSimulation();
        Assert.True(Reached(sim, 0, 20), "Africa");
        Assert.True(Reached(sim, 50, 90), "Eurasia");
        Assert.True(Reached(sim, 45, -100), "North America");
        Assert.True(Reached(sim, -25, 135), "Australia");
        Assert.True(Reached(sim, 53, -1.5), "Great Britain");
        Assert.True(Reached(sim, 36, 138), "Honshu");
        Assert.True(Reached(sim, 43.4, 142.8), "Hokkaido, by a short crossing");
        Assert.False(Reached(sim, -19, 47), "Madagascar");
        Assert.False(Reached(sim, -38.5, 176), "New Zealand");
        Assert.False(Reached(sim, 65, -18), "Iceland");
        Assert.False(Reached(sim, 21.5, -79), "Cuba");
        Assert.False(Reached(sim, 19.6, -155.5), "Hawaii, too far by sea");
        Assert.False(Reached(sim, -17.8, 178), "Fiji, too far by sea");
    }

    [Fact]
    public void Every_reached_cell_starts_with_one_full_forager_group_and_the_rest_start_empty()
    {
        Simulation sim = NewSimulation();
        var groups = new PopulationGroups(sim.State);
        double[] capacity = sim.State.DoubleField(ForagerSystem.CapacityField);
        ForagerSystem foragers = Foragers(sim);
        for (int cell = 0; cell < _world.CellCount; cell++)
        {
            if (foragers.IsReachable(cell) && capacity[cell] > 0.0)
            {
                Assert.Equal(1, groups.CountInCell(cell));
                int group = groups.Find(cell, foragers.Occupation);
                Assert.Equal(capacity[cell], groups.Size(group));
                Assert.Equal(1.0, groups.Food(group));
            }
            else
            {
                Assert.Equal(0, groups.CountInCell(cell));
            }
        }
    }

    [Fact]
    public void Coasts_rivers_and_better_biomes_feed_more_people_per_km2()
    {
        Simulation sim = NewSimulation();
        double[] capacity = sim.State.DoubleField(ForagerSystem.CapacityField);
        double Density(double lat, double lon)
        {
            int cell = _world.Grid.FindCell(lat, lon);
            return capacity[cell] / _world.Grid.AreaKm2(cell);
        }

        Assert.True(Density(25.7, 32.65) > 3.0 * Density(23.0, 12.0), "The Nile valley should feed far more than the open Sahara.");
        Assert.True(Density(48.0, 10.0) > Density(65.0, 100.0), "Temperate forest should feed more than boreal forest.");
        Assert.Equal(0.0, Density(-80.0, 0.0));
    }

    [Fact]
    public void Land_that_cannot_feed_anyone_empties_and_people_move_into_empty_land()
    {
        Simulation sim = NewSimulation();
        var groups = new PopulationGroups(sim.State);
        int forager = Foragers(sim).Occupation;
        int[] biome = sim.State.IntField(ClimateSystem.BiomeField);

        // Get past the first January, so the next yearly update (year 1) does not recalculate biomes.
        sim.Run(1);

        // Empty a block of cells in central Europe and turn one cell to ice.
        int centre = _world.Grid.FindCell(48.0, 10.0);
        var block = new List<int> { centre };
        block.AddRange(_world.Grid.Neighbours(centre).ToArray());
        foreach (int cell in block)
        {
            groups.SetSize(groups.Find(cell, forager), 0.0);
        }

        int iced = _world.Grid.FindCell(50.0, 30.0);
        int iceIndex = sim.Systems.OfType<ClimateSystem>().Single().Biomes.IndexOf("Ice");
        double before = groups.Size(groups.Find(iced, forager));
        biome[iced] = iceIndex;
        sim.Run(12);

        double[] capacity = sim.State.DoubleField(ForagerSystem.CapacityField);
        Assert.All(block.Skip(1), cell =>
        {
            double people = groups.Size(groups.Find(cell, forager));
            Assert.True(people > 0.0, "People should move into the emptied cells at the edge.");
            Assert.True(people < capacity[cell]);
        });

        int icedGroup = groups.Find(iced, forager);
        Assert.True(groups.Size(icedGroup) < before, "A cell that turned to ice should lose people.");
        Assert.Equal(0.0, groups.Food(icedGroup));
    }

    [Fact]
    public void Same_seed_gives_the_same_people_and_save_load_matches()
    {
        Simulation straight = NewSimulation(4);
        straight.Run(60 * 12);

        Simulation first = NewSimulation(4);
        first.Run(25 * 12 + 7);
        using var save = new MemoryStream();
        first.Save(save);

        Simulation second = NewSimulation(77);
        second.Load(new MemoryStream(save.ToArray()));
        second.Run(35 * 12 - 7);
        Assert.Equal(straight.StateChecksum(), second.StateChecksum());
        Assert.Equal(PeoplePerCell(straight), PeoplePerCell(second));
    }

    [Fact]
    public void Every_biome_has_a_density()
    {
        BiomeSet biomes = BiomeSet.LoadDefault();
        ForagerParameters parameters = ForagerParameters.LoadDefault();
        Assert.All(biomes.Biomes, b => Assert.True(parameters.Density.ContainsKey(b.Name), b.Name));
        Assert.Throws<InvalidDataException>(() =>
            new ForagerSystem(new ForagerParameters { Reached = parameters.Reached }, biomes, OccupationSet.LoadDefault()));
    }
}
