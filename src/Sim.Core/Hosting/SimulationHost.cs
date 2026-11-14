using System.Diagnostics;
using Sim.Core.Engine;

namespace Sim.Core.Hosting;

/// <summary>What a display is given after the simulation advances. Safe to read from any thread.</summary>
public sealed class SimFrame
{
    public SimFrame(long tick, double[]? values, double measuredTicksPerSecond)
    {
        Tick = tick;
        Values = values;
        MeasuredTicksPerSecond = measuredTicksPerSecond;
    }

    /// <summary>The next tick to be simulated at the time of the frame.</summary>
    public long Tick { get; }

    /// <summary>A copy of the per-cell values the current map mode asked for, or null.</summary>
    public double[]? Values { get; }

    public double MeasuredTicksPerSecond { get; }

    public GameDate Date => new GameDate(Tick);
}

/// <summary>
/// Runs a simulation on its own thread at a chosen speed. A display never touches the
/// simulation directly: it reads <see cref="LatestFrame"/> and sends requests through
/// <see cref="InvokeAsync{T}"/>, which run on the simulation thread between ticks.
/// </summary>
public sealed class SimulationHost : IDisposable
{
    /// <summary>Speed setting that runs ticks as fast as the machine allows.</summary>
    public const double MaxSpeed = double.PositiveInfinity;

    private const double SliceSeconds = 0.010;
    private const double PublishIntervalSeconds = 0.030;

    private readonly Simulation _simulation;
    private readonly Thread _thread;
    private readonly object _gate = new object();
    private readonly Queue<Command> _commands = new Queue<Command>();
    private readonly AutoResetEvent _wake = new AutoResetEvent(false);

    private double _speed;
    private bool _stop;
    private Func<Simulation, double[]?>? _extractor;
    private SimFrame _latest;
    private Exception? _fault;

    public SimulationHost(Simulation simulation)
    {
        _simulation = simulation;
        _latest = new SimFrame(simulation.Tick, null, 0.0);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Simulation" };
        _thread.Start();
    }

    /// <summary>Target speed in ticks (months) per second. 0 is paused. Starts paused.</summary>
    public double TicksPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _speed;
            }
        }

        set
        {
            if (double.IsNaN(value) || value < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Speed cannot be negative.");
            }

            lock (_gate)
            {
                _speed = value;
            }

            _wake.Set();
        }
    }

    /// <summary>The most recent frame published by the simulation thread.</summary>
    public SimFrame LatestFrame => Volatile.Read(ref _latest);

    /// <summary>Set if a system threw an error. The simulation is paused when this happens.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Runs a function on the simulation thread between ticks and returns its result.</summary>
    public Task<T> InvokeAsync<T>(Func<Simulation, T> function)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new Command(
            () =>
            {
                try
                {
                    completion.TrySetResult(function(_simulation));
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            },
            () => completion.TrySetCanceled());

        lock (_gate)
        {
            if (_stop)
            {
                throw new ObjectDisposedException(nameof(SimulationHost));
            }

            _commands.Enqueue(command);
        }

        _wake.Set();
        return completion.Task;
    }

    public Task InvokeAsync(Action<Simulation> action) =>
        InvokeAsync(simulation =>
        {
            action(simulation);
            return true;
        });

    /// <summary>
    /// Chooses which per-cell values each frame carries. The function runs on the simulation
    /// thread and must return a fresh copy. Pass null for frames with no values.
    /// </summary>
    public Task SetValueExtractor(Func<Simulation, double[]?>? extractor) =>
        InvokeAsync(_ => { _extractor = extractor; });

    public Task SaveAsync(string path) => InvokeAsync(simulation => simulation.Save(path));

    public Task LoadAsync(string path) => InvokeAsync(simulation => simulation.Load(path));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stop)
            {
                return;
            }

            _stop = true;
        }

        _wake.Set();
        _thread.Join();
        _wake.Dispose();
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        double last = 0.0;
        double credit = 0.0;
        double rateStart = 0.0;
        long rateTicks = 0;
        double rate = 0.0;
        double lastPublish = double.NegativeInfinity;
        bool dirty = false;

        while (true)
        {
            bool changed = RunCommands(out bool stop);
            if (stop)
            {
                break;
            }

            double speed;
            lock (_gate)
            {
                speed = _speed;
            }

            double now = clock.Elapsed.TotalSeconds;
            double elapsed = now - last;
            last = now;

            long ran = 0;
            if (speed > 0.0)
            {
                long due;
                if (double.IsPositiveInfinity(speed))
                {
                    due = long.MaxValue;
                }
                else
                {
                    // Cap the backlog so a slow machine falls behind instead of freezing.
                    credit = Math.Min(credit + elapsed * speed, Math.Max(1.0, speed * 0.25));
                    due = (long)credit;
                }

                double sliceEnd = now + SliceSeconds;
                try
                {
                    while (ran < due)
                    {
                        _simulation.Step();
                        ran++;
                        if (clock.Elapsed.TotalSeconds >= sliceEnd)
                        {
                            break;
                        }
                    }
                }
                catch (Exception error)
                {
                    Volatile.Write(ref _fault, error);
                    lock (_gate)
                    {
                        _speed = 0.0;
                    }

                    changed = true;
                }

                if (!double.IsPositiveInfinity(speed))
                {
                    credit -= ran;
                }
            }
            else
            {
                credit = 0.0;
            }

            rateTicks += ran;
            if (now - rateStart >= 0.5)
            {
                double newRate = rateTicks / (now - rateStart);
                if (newRate != rate)
                {
                    rate = newRate;
                    changed = true;
                }

                rateTicks = 0;
                rateStart = now;
            }

            dirty |= ran > 0 || changed;
            if (dirty && (changed || now - lastPublish >= PublishIntervalSeconds))
            {
                Volatile.Write(ref _latest, new SimFrame(_simulation.Tick, _extractor?.Invoke(_simulation), rate));
                lastPublish = now;
                dirty = false;
            }

            if (ran == 0)
            {
                _wake.WaitOne(speed > 0.0 ? 1 : 20);
            }
        }

        // Cancel anything still waiting so callers are not left hanging.
        lock (_gate)
        {
            while (_commands.Count > 0)
            {
                _commands.Dequeue().Cancel();
            }
        }
    }

    private bool RunCommands(out bool stop)
    {
        bool ranAny = false;
        while (true)
        {
            Command? command = null;
            lock (_gate)
            {
                stop = _stop;
                if (!stop && _commands.Count > 0)
                {
                    command = _commands.Dequeue();
                }
            }

            if (command == null)
            {
                return ranAny;
            }

            command.Run();
            ranAny = true;
        }
    }

    private sealed class Command
    {
        public Command(Action run, Action cancel)
        {
            Run = run;
            Cancel = cancel;
        }

        public Action Run { get; }

        public Action Cancel { get; }
    }
}
