using Sim.Core.Climate;
using Sim.Core.People;

namespace Sim.Core.Engine;

/// <summary>
/// The one place that lists the simulation's systems and fixes their update order.
/// The viewer and the headless runner both build their simulation from this list.
/// </summary>
public static class SystemCatalog
{
    /// <summary>The systems of the real simulation, in update order.</summary>
    public static IReadOnlyList<ISimSystem> CreateDefault()
    {
        ClimateSystem climate = ClimateSystem.CreateDefault();
        OccupationSet occupations = OccupationSet.LoadDefault();
        return new ISimSystem[]
        {
            climate,
            new PopulationSystem(),
            ForagerSystem.CreateDefault(climate.Biomes, occupations),
        };
    }
}
