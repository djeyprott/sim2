using Sim.Core.Engine;
using Sim.Core.Geo;
using Sim.Core.Map;
using Xunit;

namespace Sim.Core.Tests;

internal static class TestWorlds
{
    public static WorldMap Small()
    {
        GeodesicGrid grid = GeodesicGrid.Create(2);
        return new WorldMap("Test", grid, new short[grid.CellCount], new TerrainClass[grid.CellCount]);
    }
}

/// <summary>Test-only system: records when it ran and draws one random number each time.</summary>
internal sealed class RecordingSystem : ISimSystem
{
    private readonly List<string> _sharedLog;

    public RecordingSystem(string name, int intervalMonths, List<string> sharedLog)
    {
        Name = name;
        IntervalMonths = intervalMonths;
        _sharedLog = sharedLog;
    }

    public string Name { get; }

    public int IntervalMonths { get; }

    public List<long> Ticks { get; } = new List<long>();

    public List<ulong> Draws { get; } = new List<ulong>();

    public void Update(SimContext context)
    {
        Ticks.Add(context.Tick);
        Draws.Add(context.Rng.NextULong());
        _sharedLog.Add($"{context.Tick}:{Name}");
    }
}

public class GameDateTests
{
    [Theory]
    [InlineData(0L, 1, 10000L, true, "January 10000 BCE")]
    [InlineData(11L, 12, 10000L, true, "December 10000 BCE")]
    [InlineData(12L, 1, 9999L, true, "January 9999 BCE")]
    [InlineData(119999L, 12, 1L, true, "December 1 BCE")]
    [InlineData(120000L, 1, 1L, false, "January 1 CE")]
    [InlineData(144310L, 11, 2026L, false, "November 2026 CE")]
    public void Ticks_map_to_dates_with_no_year_zero(long tick, int month, long year, bool bce, string text)
    {
        var date = new GameDate(tick);
        Assert.Equal(month, date.Month);
        Assert.Equal(year, date.Year);
        Assert.Equal(bce, date.IsBce);
        Assert.Equal(text, date.ToString());
    }

    [Fact]
    public void Tick_of_year_is_the_inverse()
    {
        Assert.Equal(0L, GameDate.TickOfYear(-10000));
        Assert.Equal(119988L, GameDate.TickOfYear(-1));
        Assert.Equal(120000L, GameDate.TickOfYear(1));
        Assert.Equal("January 2026 CE", new GameDate(GameDate.TickOfYear(2026)).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => GameDate.TickOfYear(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameDate(-1));
    }
}

public class RngTests
{
    [Fact]
    public void Matches_reference_values()
    {
        // Reference values from an independent implementation of xoshiro256** seeded by SplitMix64.
        var zero = new Rng(0);
        Assert.Equal(11091344671253066420UL, zero.NextULong());
        Assert.Equal(13793997310169335082UL, zero.NextULong());
        Assert.Equal(1900383378846508768UL, zero.NextULong());

        var other = new Rng(12345);
        Assert.Equal(13720838825685603483UL, other.NextULong());
        Assert.Equal(2398916695208396998UL, other.NextULong());

        Rng stream = Rng.ForStream(42, "system:test");
        Assert.Equal(15094538491479934531UL, stream.NextULong());
        Assert.Equal(16740135820927445954UL, stream.NextULong());

        var ints = new Rng(7);
        int[] expected = { 70, 27, 83, 98, 99, 87 };
        foreach (int value in expected)
        {
            Assert.Equal(value, ints.NextInt(100));
        }

        Assert.Equal(0.7005764821796896, new Rng(7).NextDouble());
    }

    [Fact]
    public void Values_stay_in_range_and_are_spread_evenly()
    {
        var rng = new Rng(99);
        var buckets = new int[10];
        for (int i = 0; i < 100000; i++)
        {
            double d = rng.NextDouble();
            Assert.InRange(d, 0.0, 1.0);
            Assert.True(d < 1.0);
            buckets[rng.NextInt(10)]++;
            Assert.InRange(rng.NextInt(-5, 5), -5, 4);
        }

        foreach (int count in buckets)
        {
            Assert.InRange(count, 9500, 10500);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
    }

    [Fact]
    public void Streams_are_independent_and_repeatable()
    {
        Assert.Equal(Rng.ForStream(1, "a").NextULong(), Rng.ForStream(1, "a").NextULong());
        Assert.NotEqual(Rng.ForStream(1, "a").NextULong(), Rng.ForStream(1, "b").NextULong());
        Assert.NotEqual(Rng.ForStream(1, "a").NextULong(), Rng.ForStream(2, "a").NextULong());
    }

    [Fact]
    public void State_can_be_saved_and_restored()
    {
        var rng = new Rng(5);
        rng.NextULong();
        ulong[] state = rng.GetState();
        ulong next = rng.NextULong();
        ulong afterNext = rng.NextULong();

        var restored = new Rng(0);
        restored.SetState(state);
        Assert.Equal(next, restored.NextULong());
        Assert.Equal(afterNext, restored.NextULong());
    }
}

public class SimulationTests
{
    [Fact]
    public void Systems_run_in_fixed_order_at_their_intervals_over_120_ticks()
    {
        var log = new List<string>();
        var yearly = new RecordingSystem("yearly", 12, log);
        var monthly = new RecordingSystem("monthly", 1, log);
        var seasonal = new RecordingSystem("seasonal", 3, log);
        var sim = new Simulation(TestWorlds.Small(), 1, new ISimSystem[] { yearly, monthly, seasonal });

        sim.Run(120);

        Assert.Equal(120L, sim.Tick);
        Assert.Equal("January 9990 BCE", sim.Date.ToString());
        Assert.Equal(120, monthly.Ticks.Count);
        Assert.Equal(40, seasonal.Ticks.Count);
        Assert.Equal(10, yearly.Ticks.Count);
        Assert.All(seasonal.Ticks, t => Assert.Equal(0L, t % 3));
        Assert.All(yearly.Ticks, t => Assert.Equal(0L, t % 12));

        // Rebuild the expected order: tick by tick, systems in registration order.
        var expected = new List<string>();
        for (long tick = 0; tick < 120; tick++)
        {
            if (tick % 12 == 0)
            {
                expected.Add($"{tick}:yearly");
            }

            expected.Add($"{tick}:monthly");
            if (tick % 3 == 0)
            {
                expected.Add($"{tick}:seasonal");
            }
        }

        Assert.Equal(expected, log);
    }

    [Fact]
    public void Same_seed_gives_the_same_random_draws_and_a_different_seed_does_not()
    {
        List<ulong> Run(ulong seed)
        {
            var system = new RecordingSystem("monthly", 1, new List<string>());
            var sim = new Simulation(TestWorlds.Small(), seed, new ISimSystem[] { system });
            sim.Run(50);
            return system.Draws;
        }

        Assert.Equal(Run(7), Run(7));
        Assert.NotEqual(Run(7), Run(8));
    }

    [Fact]
    public void Adding_a_system_does_not_change_what_existing_systems_draw()
    {
        var alone = new RecordingSystem("farming", 3, new List<string>());
        new Simulation(TestWorlds.Small(), 3, new ISimSystem[] { alone }).Run(60);

        var log = new List<string>();
        var withOthers = new RecordingSystem("farming", 3, log);
        var extraBefore = new RecordingSystem("war", 1, log);
        var extraAfter = new RecordingSystem("trade", 1, log);
        new Simulation(TestWorlds.Small(), 3, new ISimSystem[] { extraBefore, withOthers, extraAfter }).Run(60);

        Assert.Equal(alone.Draws, withOthers.Draws);
        Assert.NotEqual(extraBefore.Draws, extraAfter.Draws);
    }

    [Fact]
    public void Bad_system_lists_are_rejected()
    {
        var log = new List<string>();
        WorldMap map = TestWorlds.Small();
        Assert.Throws<ArgumentException>(() =>
            new Simulation(map, 1, new ISimSystem[] { new RecordingSystem("a", 2, log) }));
        Assert.Throws<ArgumentException>(() =>
            new Simulation(map, 1, new ISimSystem[] { new RecordingSystem("a", 1, log), new RecordingSystem("a", 3, log) }));
        Assert.Throws<ArgumentException>(() =>
            new Simulation(map, 1, new ISimSystem[] { new RecordingSystem("", 1, log) }));
    }
}
