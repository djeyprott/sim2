using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Map;
using Xunit;

namespace Sim.Core.Tests;

/// <summary>Test-only system: writes random values into random cells each time it runs.</summary>
internal sealed class RandomWriteSystem : ISimSystem
{
    private int[] _counts = Array.Empty<int>();
    private double[] _amounts = Array.Empty<double>();

    public RandomWriteSystem(string name, int intervalMonths)
    {
        Name = name;
        IntervalMonths = intervalMonths;
    }

    public string Name { get; }

    public int IntervalMonths { get; }

    public void Register(WorldState state)
    {
        _counts = state.AddIntField(Name + ".counts");
        _amounts = state.AddDoubleField(Name + ".amounts");
    }

    public void Update(SimContext context)
    {
        for (int i = 0; i < 5; i++)
        {
            int cell = context.Rng.NextInt(context.Map.CellCount);
            _counts[cell] += context.Rng.NextInt(1, 100);
            _amounts[cell] += context.Rng.NextDouble() * context.Date.Month;
        }
    }
}

public class SaveLoadTests
{
    private static readonly WorldMap Map = TestWorlds.Small();

    private static Simulation NewSimulation(ulong seed, WorldMap? map = null) =>
        new Simulation(map ?? Map, seed, new ISimSystem[]
        {
            new RandomWriteSystem("yearly", 12),
            new RandomWriteSystem("monthly", 1),
            new RandomWriteSystem("seasonal", 3),
        });

    private static byte[] SaveToBytes(Simulation simulation)
    {
        using var stream = new MemoryStream();
        simulation.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void Same_seed_gives_the_same_checksum_and_a_different_seed_does_not()
    {
        Simulation a = NewSimulation(11);
        Simulation b = NewSimulation(11);
        Simulation c = NewSimulation(12);
        a.Run(500);
        b.Run(500);
        c.Run(500);

        Assert.Equal(a.StateChecksum(), b.StateChecksum());
        Assert.NotEqual(a.StateChecksum(), c.StateChecksum());
    }

    [Fact]
    public void Checksum_changes_as_the_run_advances()
    {
        Simulation sim = NewSimulation(1);
        string before = sim.StateChecksum();
        sim.Step();
        Assert.NotEqual(before, sim.StateChecksum());
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 1)]
    [InlineData(250, 350)]
    [InlineData(1201, 77)]
    public void Save_load_then_run_equals_an_uninterrupted_run(int ticksBefore, int ticksAfter)
    {
        Simulation straight = NewSimulation(5);
        straight.Run(ticksBefore + ticksAfter);

        Simulation first = NewSimulation(5);
        first.Run(ticksBefore);
        byte[] save = SaveToBytes(first);

        // Load into a simulation created with a different seed: the save must fully replace it.
        Simulation second = NewSimulation(999);
        second.Run(13);
        second.Load(new MemoryStream(save));
        Assert.Equal(first.StateChecksum(), second.StateChecksum());
        Assert.Equal(first.Tick, second.Tick);
        Assert.Equal(5UL, second.Seed);

        second.Run(ticksAfter);
        Assert.Equal(straight.Tick, second.Tick);
        Assert.Equal(straight.Date.ToString(), second.Date.ToString());
        Assert.Equal(straight.StateChecksum(), second.StateChecksum());
    }

    [Fact]
    public void Saving_does_not_disturb_the_run()
    {
        Simulation a = NewSimulation(3);
        Simulation b = NewSimulation(3);
        a.Run(40);
        b.Run(40);
        SaveToBytes(a);
        a.Run(40);
        b.Run(40);
        Assert.Equal(b.StateChecksum(), a.StateChecksum());
    }

    [Fact]
    public void Long_run_of_12000_years_completes()
    {
        Simulation sim = NewSimulation(1);
        sim.Run(144000);
        Assert.Equal("January 2001 CE", sim.Date.ToString());
    }

    [Fact]
    public void A_save_that_does_not_fit_is_rejected_and_changes_nothing()
    {
        Simulation source = NewSimulation(5);
        source.Run(30);
        byte[] save = SaveToBytes(source);

        // Different map.
        GeodesicGrid grid = GeodesicGrid.Create(2);
        var elevation = new short[grid.CellCount];
        elevation[0] = 1;
        var otherMap = new WorldMap("Other", grid, elevation, new TerrainClass[grid.CellCount]);
        Simulation wrongMap = NewSimulation(5, otherMap);
        string untouched = wrongMap.StateChecksum();
        Assert.Throws<InvalidDataException>(() => wrongMap.Load(new MemoryStream(save)));
        Assert.Equal(untouched, wrongMap.StateChecksum());

        // Different systems.
        var wrongSystems = new Simulation(Map, 5, new ISimSystem[] { new RandomWriteSystem("monthly", 1) });
        Assert.Throws<InvalidDataException>(() => wrongSystems.Load(new MemoryStream(save)));

        // Not a save at all, and a save cut short.
        Simulation target = NewSimulation(5);
        untouched = target.StateChecksum();
        Assert.Throws<InvalidDataException>(() => target.Load(new MemoryStream(new byte[100])));
        Assert.Throws<InvalidDataException>(() => target.Load(new MemoryStream(save, 0, save.Length / 2)));
        Assert.Throws<InvalidDataException>(() => target.Load(new MemoryStream(save, 0, 5)));
        Assert.Equal(untouched, target.StateChecksum());
    }
}

public class WorldStateTests
{
    private sealed class FieldSystem : ISimSystem
    {
        private readonly Action<WorldState> _register;

        public FieldSystem(Action<WorldState> register)
        {
            _register = register;
        }

        public string Name => "fields";

        public int IntervalMonths => 1;

        public void Register(WorldState state) => _register(state);

        public void Update(SimContext context)
        {
        }
    }

    [Fact]
    public void Fields_are_per_cell_start_at_zero_and_can_be_found_by_name()
    {
        int[]? ints = null;
        double[]? doubles = null;
        var sim = new Simulation(TestWorlds.Small(), 1, new ISimSystem[]
        {
            new FieldSystem(state =>
            {
                ints = state.AddIntField("people");
                doubles = state.AddDoubleField("grain");
            }),
        });

        Assert.Equal(sim.Map.CellCount, ints!.Length);
        Assert.Equal(sim.Map.CellCount, doubles!.Length);
        Assert.All(ints, v => Assert.Equal(0, v));
        Assert.Same(ints, sim.State.IntField("people"));
        Assert.Same(doubles, sim.State.DoubleField("grain"));
        Assert.Equal(new[] { "people", "grain" }, sim.State.FieldNames);
        Assert.Throws<InvalidOperationException>(() => sim.State.IntField("grain"));
        Assert.Throws<KeyNotFoundException>(() => sim.State.IntField("missing"));
    }

    [Fact]
    public void Duplicate_and_late_fields_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new Simulation(TestWorlds.Small(), 1, new ISimSystem[]
        {
            new FieldSystem(state =>
            {
                state.AddIntField("people");
                state.AddDoubleField("people");
            }),
        }));

        var sim = new Simulation(TestWorlds.Small(), 1, new ISimSystem[] { new FieldSystem(_ => { }) });
        Assert.Throws<InvalidOperationException>(() => sim.State.AddIntField("late"));
    }
}
