using Sim.Core.Engine;
using Xunit;

namespace Sim.Core.Tests;

/// <summary>Test-only system with a table of things and a setting, changed at random each month.</summary>
internal sealed class TableSystem : ISimSystem
{
    private EntityTable _things = null!;
    private IntColumn _count = null!;
    private DoubleColumn _amount = null!;
    private StringColumn _label = null!;
    private WorldState _state = null!;

    public string Name => "tables";

    public int IntervalMonths => 1;

    public void Register(WorldState state)
    {
        _state = state;
        _things = state.AddTable("things");
        _count = _things.AddIntColumn("count");
        _amount = _things.AddDoubleColumn("amount");
        _label = _things.AddStringColumn("label");
    }

    public void Initialize(SimContext context)
    {
        _state.SetSetting("mode", "test");
        for (int i = 0; i < 3; i++)
        {
            int id = _things.Add();
            _label[id] = "start " + id;
        }
    }

    public void Update(SimContext context)
    {
        Rng rng = context.Rng;
        if (rng.Chance(0.6) || _things.LiveCount == 0)
        {
            int id = _things.Add();
            _count[id] = rng.NextInt(1000);
            _amount[id] = rng.NextDouble();
            if (rng.Chance(0.5))
            {
                _label[id] = "thing " + id;
            }
        }
        else
        {
            int id = rng.NextInt(_things.IdCount);
            if (_things.IsAlive(id))
            {
                _things.Remove(id);
            }
        }

        _state.SetSetting("last", context.Date.ToString());
    }
}

public class TableTests
{
    private static Simulation NewSimulation(ulong seed) =>
        new Simulation(TestWorlds.Small(), seed, new ISimSystem[] { new TableSystem(), new RandomWriteSystem("monthly", 1) });

    [Fact]
    public void Rows_get_stable_ids_that_are_never_reused()
    {
        var sim = new Simulation(TestWorlds.Small(), 1, new ISimSystem[] { new TableSystem() });
        EntityTable things = sim.State.Table("things");
        Assert.Equal(3, things.IdCount);
        Assert.Equal("start 1", things.StringColumn("label")[1]);

        int a = things.Add();
        int b = things.Add();
        Assert.Equal(3, a);
        Assert.Equal(4, b);
        things.Remove(a);
        Assert.False(things.IsAlive(a));
        Assert.True(things.IsAlive(b));
        Assert.Equal(5, things.IdCount);
        Assert.Equal(4, things.LiveCount);
        Assert.Equal(5, things.Add());
        Assert.Throws<InvalidOperationException>(() => things.Remove(a));
        Assert.Throws<ArgumentOutOfRangeException>(() => things.IntColumn("count")[6]);
        Assert.False(things.IsAlive(99));

        // Growing past the first block keeps existing values.
        things.IntColumn("count")[b] = 42;
        for (int i = 0; i < 100; i++)
        {
            things.Add();
        }

        Assert.Equal(42, things.IntColumn("count")[b]);
        Assert.Equal(106, things.IdCount);
    }

    [Fact]
    public void Settings_are_kept_sorted_and_can_be_replaced()
    {
        var sim = new Simulation(TestWorlds.Small(), 1, new ISimSystem[] { new TableSystem() });
        sim.State.SetSetting("zebra", "1");
        sim.State.SetSetting("apple", "2");
        sim.State.SetSetting("mode", "changed");
        string[] keys = sim.State.Settings.Select(s => s.Key).ToArray();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), keys);
        Assert.Contains("apple", keys);
        Assert.Contains("zebra", keys);
        Assert.Equal("changed", sim.State.GetSetting("mode"));
        Assert.Null(sim.State.GetSetting("missing"));
    }

    [Fact]
    public void Tables_and_settings_are_part_of_the_checksum()
    {
        Simulation a = NewSimulation(4);
        Simulation b = NewSimulation(4);
        Assert.Equal(a.StateChecksum(), b.StateChecksum());

        b.State.Table("things").Add();
        Assert.NotEqual(a.StateChecksum(), b.StateChecksum());

        Simulation c = NewSimulation(4);
        c.State.SetSetting("extra", "x");
        Assert.NotEqual(a.StateChecksum(), c.StateChecksum());
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(37, 90)]
    [InlineData(400, 13)]
    public void Save_load_then_run_equals_an_uninterrupted_run(int ticksBefore, int ticksAfter)
    {
        Simulation straight = NewSimulation(8);
        straight.Run(ticksBefore + ticksAfter);

        Simulation first = NewSimulation(8);
        first.Run(ticksBefore);
        using var save = new MemoryStream();
        first.Save(save);

        Simulation second = NewSimulation(123);
        second.Run(29);
        second.Load(new MemoryStream(save.ToArray()));
        Assert.Equal(first.StateChecksum(), second.StateChecksum());
        Assert.Equal(first.State.Table("things").IdCount, second.State.Table("things").IdCount);
        Assert.Equal(first.State.GetSetting("last"), second.State.GetSetting("last"));

        second.Run(ticksAfter);
        Assert.Equal(straight.StateChecksum(), second.StateChecksum());
    }

    [Fact]
    public void Late_or_duplicate_tables_and_columns_are_rejected()
    {
        var sim = new Simulation(TestWorlds.Small(), 1, new ISimSystem[] { new TableSystem() });
        Assert.Throws<InvalidOperationException>(() => sim.State.AddTable("late"));
        Assert.Throws<InvalidOperationException>(() => sim.State.Table("things").AddIntColumn("late"));
        Assert.Throws<KeyNotFoundException>(() => sim.State.Table("missing"));
        Assert.Throws<InvalidOperationException>(() => sim.State.Table("things").IntColumn("label"));

        Assert.Throws<ArgumentException>(() => new Simulation(TestWorlds.Small(), 1, new ISimSystem[]
        {
            new TableSystem(),
            new DuplicateTableSystem(),
        }));
    }

    [Fact]
    public void A_save_with_different_tables_is_rejected_and_changes_nothing()
    {
        Simulation source = NewSimulation(2);
        source.Run(20);
        using var save = new MemoryStream();
        source.Save(save);

        // Same system names, but the table system registers different columns.
        var target = new Simulation(TestWorlds.Small(), 2, new ISimSystem[]
        {
            new OtherColumnsTableSystem(),
            new RandomWriteSystem("monthly", 1),
        });
        string before = target.StateChecksum();
        Assert.Throws<InvalidDataException>(() => target.Load(new MemoryStream(save.ToArray())));
        Assert.Equal(before, target.StateChecksum());
    }

    private sealed class DuplicateTableSystem : ISimSystem
    {
        public string Name => "duplicate";

        public int IntervalMonths => 12;

        public void Register(WorldState state) => state.AddTable("things");

        public void Update(SimContext context)
        {
        }
    }

    private sealed class OtherColumnsTableSystem : ISimSystem
    {
        public string Name => "tables";

        public int IntervalMonths => 1;

        public void Register(WorldState state)
        {
            EntityTable things = state.AddTable("things");
            things.AddIntColumn("count");
            things.AddIntColumn("other");
        }

        public void Update(SimContext context)
        {
        }
    }
}
