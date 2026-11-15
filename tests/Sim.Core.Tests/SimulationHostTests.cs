using System.Diagnostics;
using Sim.Core.Engine;
using Sim.Core.Hosting;
using Xunit;

namespace Sim.Core.Tests;

public class SimulationHostTests
{
    private static Simulation NewSimulation(ulong seed = 5) =>
        new Simulation(TestWorlds.Small(), seed, new ISimSystem[]
        {
            new RandomWriteSystem("yearly", 12),
            new RandomWriteSystem("monthly", 1),
        });

    private static bool WaitFor(Func<bool> condition, double seconds = 10.0)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    [Fact]
    public void Starts_paused_runs_at_max_speed_and_pauses_again()
    {
        using var host = new SimulationHost(NewSimulation());
        Thread.Sleep(100);
        Assert.Equal(0L, host.LatestFrame.Tick);
        Assert.Equal(0.0, host.TicksPerSecond);

        host.TicksPerSecond = SimulationHost.MaxSpeed;
        Assert.True(WaitFor(() => host.LatestFrame.Tick >= 1000), "The simulation did not advance.");

        host.TicksPerSecond = 0.0;
        long paused = host.InvokeAsync(simulation => simulation.Tick).GetAwaiter().GetResult();
        Thread.Sleep(150);
        Assert.Equal(paused, host.InvokeAsync(simulation => simulation.Tick).GetAwaiter().GetResult());
        Assert.True(WaitFor(() => host.LatestFrame.Tick == paused), "The last frame does not show the paused tick.");
        Assert.Null(host.Fault);
    }

    [Fact]
    public void Finite_speed_is_roughly_kept()
    {
        using var host = new SimulationHost(NewSimulation());
        var watch = Stopwatch.StartNew();
        host.TicksPerSecond = 120.0;
        Thread.Sleep(600);
        host.TicksPerSecond = 0.0;
        long ticks = host.InvokeAsync(simulation => simulation.Tick).GetAwaiter().GetResult();
        double seconds = watch.Elapsed.TotalSeconds;

        Assert.InRange(ticks, 10, (long)(120.0 * seconds) + 35);
    }

    [Fact]
    public void Hosted_run_matches_a_plain_run_of_the_same_length()
    {
        using var host = new SimulationHost(NewSimulation(9));
        host.TicksPerSecond = SimulationHost.MaxSpeed;
        Assert.True(WaitFor(() => host.LatestFrame.Tick >= 500));
        host.TicksPerSecond = 0.0;

        (long Tick, string Checksum) hosted = host
            .InvokeAsync(simulation => (simulation.Tick, simulation.StateChecksum()))
            .GetAwaiter().GetResult();

        Simulation plain = NewSimulation(9);
        plain.Run(hosted.Tick);
        Assert.Equal(plain.StateChecksum(), hosted.Checksum);
    }

    [Fact]
    public void Save_and_load_through_the_host()
    {
        string path = Path.Combine(Path.GetTempPath(), "sim2-host-test-" + Guid.NewGuid().ToString("N") + ".sav");
        try
        {
            string savedChecksum;
            long savedTick;
            using (var host = new SimulationHost(NewSimulation(3)))
            {
                host.TicksPerSecond = SimulationHost.MaxSpeed;
                Assert.True(WaitFor(() => host.LatestFrame.Tick >= 200));
                host.TicksPerSecond = 0.0;
                host.SaveAsync(path).GetAwaiter().GetResult();
                (savedTick, savedChecksum) = host
                    .InvokeAsync(simulation => (simulation.Tick, simulation.StateChecksum()))
                    .GetAwaiter().GetResult();
            }

            using var second = new SimulationHost(NewSimulation(77));
            second.LoadAsync(path).GetAwaiter().GetResult();
            Assert.Equal(savedChecksum, second.InvokeAsync(simulation => simulation.StateChecksum()).GetAwaiter().GetResult());
            Assert.True(WaitFor(() => second.LatestFrame.Tick == savedTick), "The frame does not show the loaded date.");

            // A failed load reports the error to the caller and leaves the host working.
            File.WriteAllBytes(path, new byte[50]);
            Assert.Throws<InvalidDataException>(() => second.LoadAsync(path).GetAwaiter().GetResult());
            Assert.Equal(savedChecksum, second.InvokeAsync(simulation => simulation.StateChecksum()).GetAwaiter().GetResult());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Frames_carry_a_copy_of_the_requested_values()
    {
        using var host = new SimulationHost(NewSimulation());
        Assert.Null(host.LatestFrame.Values);

        host.SetValueExtractor(simulation => (double[])simulation.State.DoubleField("monthly.amounts").Clone())
            .GetAwaiter().GetResult();
        host.TicksPerSecond = SimulationHost.MaxSpeed;
        Assert.True(WaitFor(() =>
        {
            double[]? values = host.LatestFrame.Values;
            return values != null && values.Any(v => v > 0.0);
        }));

        host.TicksPerSecond = 0.0;
        double[] live = host.InvokeAsync(simulation => simulation.State.DoubleField("monthly.amounts")).GetAwaiter().GetResult();
        Assert.True(WaitFor(() => host.LatestFrame.Values != null && host.LatestFrame.Values.SequenceEqual(live)));
        Assert.NotSame(live, host.LatestFrame.Values);
    }

    [Fact]
    public void Frames_carry_the_extractor_tag_and_summary_values()
    {
        using var host = new SimulationHost(NewSimulation());
        host.SetValueExtractor(simulation => new double[] { 1.0 }, tag: 7).GetAwaiter().GetResult();
        host.SetSummaryExtractor(simulation => new double[] { simulation.Tick }).GetAwaiter().GetResult();
        Assert.True(WaitFor(() => host.LatestFrame.ValuesTag == 7 && host.LatestFrame.Summary != null));

        host.TicksPerSecond = SimulationHost.MaxSpeed;
        Assert.True(WaitFor(() => host.LatestFrame.Summary![0] > 100.0));
        host.TicksPerSecond = 0.0;
    }

    [Fact]
    public void A_failing_system_pauses_the_host_and_reports_the_fault()
    {
        var simulation = new Simulation(TestWorlds.Small(), 1, new ISimSystem[] { new ThrowingSystem() });
        using var host = new SimulationHost(simulation);
        host.TicksPerSecond = SimulationHost.MaxSpeed;
        Assert.True(WaitFor(() => host.Fault != null));
        Assert.IsType<InvalidOperationException>(host.Fault);
        Assert.Equal(0.0, host.TicksPerSecond);
    }

    [Fact]
    public void Disposed_host_refuses_new_requests()
    {
        var host = new SimulationHost(NewSimulation());
        host.Dispose();
        host.Dispose();
        bool refused = false;
        try
        {
            host.InvokeAsync(simulation => simulation.Tick);
        }
        catch (ObjectDisposedException)
        {
            refused = true;
        }

        Assert.True(refused);
    }

    private sealed class ThrowingSystem : ISimSystem
    {
        public string Name => "throwing";

        public int IntervalMonths => 1;

        public void Register(WorldState state)
        {
        }

        public void Update(SimContext context)
        {
            if (context.Tick == 3)
            {
                throw new InvalidOperationException("Deliberate test failure.");
            }
        }
    }
}
