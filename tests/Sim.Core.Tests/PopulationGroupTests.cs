using Sim.Core.Engine;
using Sim.Core.People;
using Xunit;

namespace Sim.Core.Tests;

public class PopulationGroupTests
{
    private static (Simulation Sim, PopulationGroups Groups) NewWorld()
    {
        var sim = new Simulation(TestWorlds.Small(), 1, new ISimSystem[] { new PopulationSystem() });
        return (sim, new PopulationGroups(sim.State));
    }

    [Fact]
    public void Occupations_load_with_class_and_place()
    {
        OccupationSet occupations = OccupationSet.LoadDefault();
        Occupation forager = occupations.Get(occupations.IndexOf(OccupationSet.Forager));
        Assert.Equal(SocialClass.Lower, forager.Class);
        Assert.False(forager.LivesInSettlement);
        Assert.Equal(1, occupations.IndexOf(OccupationSet.Farmer));
        Assert.Throws<InvalidDataException>(() => OccupationSet.Parse("{ \"occupations\": [ { \"name\": \"A\", \"class\": \"royal\", \"livesIn\": \"countryside\" } ] }"));
    }

    [Fact]
    public void Same_traits_in_a_cell_give_the_same_group()
    {
        (_, PopulationGroups groups) = NewWorld();
        int a = groups.FindOrAdd(5, 0);
        int b = groups.FindOrAdd(5, 1);
        Assert.NotEqual(a, b);
        Assert.Equal(a, groups.FindOrAdd(5, 0));
        Assert.Equal(-1, groups.Find(6, 0));
        Assert.Equal(2, groups.CountInCell(5));
        Assert.Equal(5, groups.Cell(b));
        Assert.Equal(1, groups.Occupation(b));
        Assert.Equal(1.0, groups.Food(a));
    }

    [Fact]
    public void Adding_people_averages_food_by_size()
    {
        (_, PopulationGroups groups) = NewWorld();
        int g = groups.FindOrAdd(3, 0);
        groups.AddPeople(g, 100.0, 1.0);
        groups.AddPeople(g, 300.0, 0.2);
        Assert.Equal(400.0, groups.Size(g));
        Assert.Equal(0.4, groups.Food(g), 9);
        Assert.Equal(400.0, groups.CellTotal(3));
        Assert.Equal(400.0, groups.WorldTotal());
        Assert.Equal(0.0, groups.WorldTotal(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => groups.SetSize(g, -1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => groups.SetSize(g, double.NaN));
    }

    [Fact]
    public void A_full_cell_first_drops_an_empty_group()
    {
        (_, PopulationGroups groups) = NewWorld();
        for (int occupation = 0; occupation < PopulationGroups.MaxGroupsPerCell; occupation++)
        {
            int g = groups.FindOrAdd(2, occupation);
            groups.AddPeople(g, occupation == 3 ? 0.0 : 10.0 + occupation, 1.0);
        }

        int extra = groups.FindOrAdd(2, 20);
        Assert.Equal(PopulationGroups.MaxGroupsPerCell, groups.CountInCell(2));
        Assert.Equal(-1, groups.Find(2, 3));
        Assert.Equal(extra, groups.Find(2, 20));
        Assert.Equal(Enumerable.Range(0, 8).Where(o => o != 3).Sum(o => 10.0 + o), groups.CellTotal(2));
    }

    [Fact]
    public void A_full_cell_with_no_empty_group_merges_its_smallest_into_the_largest()
    {
        (_, PopulationGroups groups) = NewWorld();
        for (int occupation = 0; occupation < PopulationGroups.MaxGroupsPerCell; occupation++)
        {
            int g = groups.FindOrAdd(2, occupation);
            groups.AddPeople(g, 100.0 + occupation, 1.0);
        }

        groups.AddPeople(groups.Find(2, 4), 1000.0, 0.0);
        double before = groups.CellTotal(2);
        groups.FindOrAdd(2, 20);

        Assert.Equal(PopulationGroups.MaxGroupsPerCell, groups.CountInCell(2));
        Assert.Equal(-1, groups.Find(2, 0));
        Assert.Equal(1104.0 + 100.0, groups.Size(groups.Find(2, 4)));
        Assert.Equal(before, groups.CellTotal(2));
    }

    [Fact]
    public void Groups_are_saved_and_loaded()
    {
        (Simulation sim, PopulationGroups groups) = NewWorld();
        for (int cell = 0; cell < 10; cell++)
        {
            groups.AddPeople(groups.FindOrAdd(cell, cell % 3), 50.0 * cell, 0.5);
        }

        groups.FindOrAdd(4, 7);
        using var save = new MemoryStream();
        sim.Save(save);

        (Simulation other, PopulationGroups loaded) = NewWorld();
        other.Load(new MemoryStream(save.ToArray()));
        Assert.Equal(sim.StateChecksum(), other.StateChecksum());
        for (int cell = 0; cell < 10; cell++)
        {
            Assert.Equal(groups.CellTotal(cell), loaded.CellTotal(cell));
            Assert.Equal(groups.CountInCell(cell), loaded.CountInCell(cell));
        }

        Assert.True(loaded.Find(4, 7) >= 0);
    }
}
