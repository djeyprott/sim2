namespace Sim.Core.Engine;

/// <summary>
/// The one place that lists the simulation's systems and fixes their update order.
/// The viewer and the headless runner both build their simulation from this list.
/// </summary>
public static class SystemCatalog
{
    /// <summary>The systems of the real simulation, in update order. Empty until milestone 1.</summary>
    public static IReadOnlyList<ISimSystem> CreateDefault() => Array.Empty<ISimSystem>();
}
