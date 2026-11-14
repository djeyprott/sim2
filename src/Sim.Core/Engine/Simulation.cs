using Sim.Core.Map;

namespace Sim.Core.Engine;

/// <summary>
/// Advances a world one month at a time. On each tick, systems run in the order they were
/// registered, and a system with interval N runs on ticks divisible by N. Yearly systems
/// therefore run in January and seasonal ones in January, April, July and October.
/// </summary>
public sealed class Simulation
{
    private readonly ISimSystem[] _systems;
    private readonly SimContext[] _contexts;

    /// <param name="map">The fixed physical map.</param>
    /// <param name="seed">World seed. The same seed gives the same history.</param>
    /// <param name="systems">Systems in their fixed update order.</param>
    public Simulation(WorldMap map, ulong seed, IReadOnlyList<ISimSystem> systems)
    {
        Map = map;
        Seed = seed;
        _systems = new ISimSystem[systems.Count];
        _contexts = new SimContext[systems.Count];

        for (int i = 0; i < systems.Count; i++)
        {
            ISimSystem system = systems[i];
            if (string.IsNullOrWhiteSpace(system.Name))
            {
                throw new ArgumentException("Every system needs a name.", nameof(systems));
            }

            if (system.IntervalMonths != 1 && system.IntervalMonths != 3 && system.IntervalMonths != 12)
            {
                throw new ArgumentException(
                    $"System '{system.Name}' has interval {system.IntervalMonths}; it must be 1, 3 or 12 months.",
                    nameof(systems));
            }

            for (int j = 0; j < i; j++)
            {
                if (_systems[j].Name == system.Name)
                {
                    throw new ArgumentException($"Two systems are named '{system.Name}'.", nameof(systems));
                }
            }

            _systems[i] = system;
            _contexts[i] = new SimContext(map, Rng.ForStream(seed, "system:" + system.Name));
        }
    }

    public WorldMap Map { get; }

    public ulong Seed { get; }

    /// <summary>The next tick to be simulated. Starts at 0.</summary>
    public long Tick { get; private set; }

    public GameDate Date => new GameDate(Tick);

    public IReadOnlyList<ISimSystem> Systems => _systems;

    /// <summary>Simulates one month.</summary>
    public void Step()
    {
        for (int i = 0; i < _systems.Length; i++)
        {
            if (Tick % _systems[i].IntervalMonths == 0)
            {
                _contexts[i].Tick = Tick;
                _systems[i].Update(_contexts[i]);
            }
        }

        Tick++;
    }

    /// <summary>Simulates the given number of months.</summary>
    public void Run(long ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), "Cannot run backwards.");
        }

        for (long t = 0; t < ticks; t++)
        {
            Step();
        }
    }
}
