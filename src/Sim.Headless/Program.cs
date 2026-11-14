using System.Diagnostics;
using System.Globalization;
using Sim.Core.Engine;
using Sim.Core.Map;

namespace Sim.Headless;

/// <summary>Runs the simulation with no window and reports speed and the final state checksum.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or FormatException)
        {
            Console.Error.WriteLine("Error: " + error.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int a = 0; a < args.Length; a += 2)
        {
            if (a + 1 >= args.Length || !args[a].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Usage: sim2-headless [--world FILE] [--seed N] [--years N] [--load FILE] [--save FILE]");
            }

            options[args[a]] = args[a + 1];
        }

        string worldPath = options.TryGetValue("--world", out string? w)
            ? w
            : Path.Combine(AppContext.BaseDirectory, "earth.world");
        ulong seed = options.TryGetValue("--seed", out string? s)
            ? ulong.Parse(s, CultureInfo.InvariantCulture)
            : 1UL;
        long years = options.TryGetValue("--years", out string? y)
            ? long.Parse(y, CultureInfo.InvariantCulture)
            : 100L;
        if (years < 0)
        {
            throw new ArgumentException("--years cannot be negative.");
        }

        Console.WriteLine($"Loading world {worldPath}");
        WorldMap map = WorldMap.Load(worldPath);
        Console.WriteLine($"World '{map.Name}', {map.CellCount} cells, checksum {map.Checksum}");

        var simulation = new Simulation(map, seed, SystemCatalog.CreateDefault());
        if (options.TryGetValue("--load", out string? loadPath))
        {
            simulation.Load(loadPath);
            Console.WriteLine($"Loaded {loadPath}");
        }

        Console.WriteLine($"Seed {simulation.Seed}, starting at {simulation.Date}, {simulation.Systems.Count} systems");

        long ticks = years * GameDate.MonthsPerYear;
        var watch = Stopwatch.StartNew();
        simulation.Run(ticks);
        watch.Stop();

        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 1e-9);
        Console.WriteLine($"Ran {ticks} ticks ({years} years) in {seconds:F3} s: {ticks / seconds:F0} ticks per second");
        Console.WriteLine($"Ended at {simulation.Date}");
        Console.WriteLine($"State checksum {simulation.StateChecksum()}");

        if (options.TryGetValue("--save", out string? savePath))
        {
            simulation.Save(savePath);
            Console.WriteLine($"Saved {savePath}");
        }

        return 0;
    }
}
