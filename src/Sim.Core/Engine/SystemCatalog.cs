using Sim.Core.Climate;
using Sim.Core.Farming;
using Sim.Core.People;
using Sim.Core.Settlements;

namespace Sim.Core.Engine;

/// <summary>
/// The one place that lists the simulation's systems and fixes their update order.
/// The viewer and the headless runner both build their simulation from this list.
/// </summary>
public static class SystemCatalog
{
    /// <summary>The systems of the real simulation, in update order.</summary>
    /// <param name="farming">False leaves farming out, so the world stays foragers. Used by tests.</param>
    public static IReadOnlyList<ISimSystem> CreateDefault(bool farming = true)
    {
        ClimateSystem climate = ClimateSystem.CreateDefault();
        OccupationSet occupations = OccupationSet.LoadDefault();
        var systems = new List<ISimSystem>
        {
            climate,
            new PopulationSystem(),
            ForagerSystem.CreateDefault(climate.Biomes, occupations),
        };

        if (farming)
        {
            systems.Add(FarmingSystem.CreateDefault(climate.Biomes, occupations));
            systems.Add(SettlementSystem.CreateDefault(occupations));
        }

        return systems;
    }
}
