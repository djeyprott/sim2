using System.Diagnostics;
using System.Globalization;
using Sim.Core.Engine;
using Sim.Core.Map;
using Sim.Core.Stats;

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
                    "Usage: sim2-headless [--world FILE] [--seed N] [--homelands real|random] [--years N] [--load FILE] [--save FILE] [--stats FILE.csv]");
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

        CropHomelands homelands = CropHomelands.Real;
        if (options.TryGetValue("--homelands", out string? h) && !RunSettings.TryParseHomelands(h, out homelands))
        {
            throw new ArgumentException("--homelands must be real or random.");
        }

        var settings = new RunSettings { Seed = seed, CropHomelands = homelands };
        var simulation = new Simulation(map, settings, SystemCatalog.CreateDefault());
        if (options.TryGetValue("--load", out string? loadPath))
        {
            simulation.Load(loadPath);
            Console.WriteLine($"Loaded {loadPath}");
        }

        Console.WriteLine($"{simulation.Settings.Describe()}, starting at {simulation.Date}, {simulation.Systems.Count} systems");

        long ticks = years * GameDate.MonthsPerYear;
        simulation.MeasureSystemTime = true;
        var watch = Stopwatch.StartNew();
        if (options.TryGetValue("--stats", out string? statsPath))
        {
            // One row at the start and one after each simulated year.
            var statistics = new RunStatistics(simulation);
            using (var writer = new StreamWriter(statsPath))
            {
                writer.WriteLine(statistics.Header());
                writer.WriteLine(statistics.Row());
                for (long year = 0; year < years; year++)
                {
                    simulation.Run(GameDate.MonthsPerYear);
                    writer.WriteLine(statistics.Row());
                }
            }

            string originsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statsPath)) ?? ".",
                Path.GetFileNameWithoutExtension(statsPath) + "-origins.csv");
            File.WriteAllText(originsPath, statistics.Origins());
            Console.WriteLine($"Wrote yearly statistics to {statsPath} and farming origins to {originsPath}");
        }
        else
        {
            simulation.Run(ticks);
        }

        watch.Stop();

        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 1e-9);
        Console.WriteLine($"Ran {ticks} ticks ({years} years) in {seconds:F3} s: {ticks / seconds:F0} ticks per second");
        for (int i = 0; i < simulation.Systems.Count; i++)
        {
            Console.WriteLine($"  {simulation.Systems[i].Name}: {simulation.SystemSeconds[i]:F1} s");
        }
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
