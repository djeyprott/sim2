using Sim.Core.Map;

namespace Sim.Core.Engine;

/// <summary>
/// One part of the simulation, such as farming or trade. A system declares how often it runs
/// and changes the world only inside <see cref="Update"/>.
/// </summary>
public interface ISimSystem
{
    /// <summary>Unique name. It also names the system's random stream, so do not rename a shipped system.</summary>
    string Name { get; }

    /// <summary>How often the system runs: 1 (monthly), 3 (each season) or 12 (yearly).</summary>
    int IntervalMonths { get; }

    /// <summary>Called once, before the run starts. Add this system's state fields here and keep the arrays.</summary>
    void Register(WorldState state);

    /// <summary>
    /// Called once when the simulation is created, after every system has registered and before
    /// any save is loaded into it. Set the starting state of a new run here, and build anything
    /// derived from the map that the system needs. A loaded save replaces the state afterwards.
    /// </summary>
    void Initialize(SimContext context)
    {
    }

    void Update(SimContext context);
}

/// <summary>What a system is given when it runs.</summary>
public sealed class SimContext
{
    internal SimContext(WorldMap map, WorldState state, Rng rng)
    {
        Map = map;
        State = state;
        Rng = rng;
    }

    /// <summary>The fixed physical map.</summary>
    public WorldMap Map { get; }

    /// <summary>The changing state of the world.</summary>
    public WorldState State { get; }

    /// <summary>This system's own random stream.</summary>
    public Rng Rng { get; }

    /// <summary>The tick being simulated.</summary>
    public long Tick { get; internal set; }

    public GameDate Date => new GameDate(Tick);
}
