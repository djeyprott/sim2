using Sim.Core.Climate;
using Sim.Core.Farming;
using Sim.Core.Goods;
using Sim.Core.Herding;
using Sim.Core.People;
using Sim.Core.Polities;
using Sim.Core.Sea;
using Sim.Core.Settlements;
using Sim.Core.Technologies;

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
            GoodsSystem.CreateDefault(),
            ForagerSystem.CreateDefault(climate.Biomes, occupations),
        };

        if (farming)
        {
            systems.Add(FarmingSystem.CreateDefault(climate.Biomes, occupations));
            systems.Add(HerdingSystem.CreateDefault(climate.Biomes, occupations));
            systems.Add(SettlementSystem.CreateDefault(occupations));
            systems.Add(ResourceSystem.CreateDefault(climate.Biomes, occupations));
            systems.Add(CraftSystem.CreateDefault(occupations));
            TradeSystem trade = TradeSystem.CreateDefault(occupations);
            systems.Add(trade);

            // Technology reads trade's links (rebuilt from the world state), so it is handed the system.
            systems.Add(TechnologySystem.CreateDefault(occupations, trade));

            // Settling by sea goes by the boats people know, so it comes after technology.
            systems.Add(SeafaringSystem.CreateDefault(climate.Biomes));

            // Polities (milestone 4) read settlements, stores, trade and knowledge, so they come last.
            systems.Add(PolitySystem.CreateDefault());
        }

        return systems;
    }
}
