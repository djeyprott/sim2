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
            // One row at the start and one after each simulated year; by region every 100 years.
            var statistics = new RunStatistics(simulation);
            string Beside(string suffix) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statsPath)) ?? ".",
                Path.GetFileNameWithoutExtension(statsPath) + suffix);
            using (var writer = new StreamWriter(statsPath))
            using (var regions = new StreamWriter(Beside("-regions.csv")))
            {
                writer.WriteLine(statistics.Header());
                writer.WriteLine(statistics.Row());
                regions.WriteLine(statistics.RegionHeader());
                regions.Write(statistics.RegionRows());
                for (long year = 0; year < years; year++)
                {
                    simulation.Run(GameDate.MonthsPerYear);
                    writer.WriteLine(statistics.Row());
                    if ((year + 1) % 100 == 0)
                    {
                        regions.Write(statistics.RegionRows());
                    }
                }
            }

            File.WriteAllText(Beside("-origins.csv"), statistics.Origins());
            File.WriteAllText(Beside("-herding-origins.csv"), statistics.HerdingOrigins());
            File.WriteAllText(Beside("-technology-events.csv"), statistics.TechnologyEvents());
            File.WriteAllText(Beside("-landings.csv"), statistics.Landings());
            File.WriteAllText(Beside("-polity-events.csv"), statistics.PolityEvents());
            File.WriteAllText(Beside("-polities.csv"), statistics.Polities());
            Console.WriteLine($"Wrote yearly statistics to {statsPath}, and beside it statistics by region, farming and herding origins, technology events, landings and polity events");
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
            if (simulation.Systems[i] is Sim.Core.Technologies.TechnologySystem technology)
            {
                var events = technology.Events();
                Console.WriteLine($"    {events.Count(e => e.Kind == Sim.Core.Technologies.TechnologyEventKind.First)} first discoveries, "
                    + $"{events.Count(e => e.Kind == Sim.Core.Technologies.TechnologyEventKind.Independent)} independent, "
                    + $"{events.Count(e => e.Kind == Sim.Core.Technologies.TechnologyEventKind.Lost)} losses; "
                    + $"{technology.DiscoveryChecks:N0} discovery checks, {technology.GroupVisits:N0} group visits");
            }

            if (simulation.Systems[i] is Sim.Core.Polities.PolitySystem polities)
            {
                Console.WriteLine($"    of which trade partners {polities.PartnersSeconds:F1} s, growing {polities.GrowSeconds:F1} s, keeping whole {polities.WholeSeconds:F1} s, "
                    + $"founding {polities.FoundSeconds:F1} s, war {polities.WarSeconds:F1} s, collapse {polities.CollapseSeconds:F1} s, tribute and levels {polities.RuleSeconds:F1} s");
            }

            if (simulation.Systems[i] is Sim.Core.Goods.TradeSystem trade)
            {
                Console.WriteLine($"    of which links {trade.BuildSeconds:F1} s, prices {trade.PriceSeconds:F1} s, trading {trade.LoopSeconds:F1} s ({trade.Sends:N0} sends over {trade.LinkGoodVisits:N0} link-goods), gap measure {trade.GapSeconds:F1} s");
            }
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
