using System.Globalization;
using System.Runtime.InteropServices;
using Sim.Core.Geo;
using Sim.Core.Map;

namespace Sim.Tools.EarthImport;

/// <summary>
/// Turns a global elevation raster into a world file.
/// Input: raw 16-bit little-endian elevations in metres, rows from north to south,
/// columns from 180 W to 180 E, each sample at the centre of its pixel.
/// </summary>
public static class Program
{
    // Standard deviation of elevation inside a cell, in metres, at which land counts as hills or mountains.
    private const double HillsReliefMetres = 80.0;
    private const double MountainsReliefMetres = 300.0;

    // Land at or above this mean elevation counts as mountains even when flat (high plateaus).
    private const double HighPlateauMetres = 3000.0;

    private const double PathStepKm = 5.0;

    public static int Main(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int a = 0; a + 1 < args.Length; a += 2)
        {
            options[args[a]] = args[a + 1];
        }

        string elevationPath = Require(options, "--elevation");
        int width = int.Parse(Require(options, "--width"), CultureInfo.InvariantCulture);
        int height = int.Parse(Require(options, "--height"), CultureInfo.InvariantCulture);
        string overridesPath = Require(options, "--overrides");
        string outPath = Require(options, "--out");
        options.TryGetValue("--preview", out string? previewPath);

        Console.WriteLine("Building grid...");
        GeodesicGrid grid = GeodesicGrid.Create(GeodesicGrid.EarthFrequency);
        int count = grid.CellCount;

        // Per-cell sums over the samples falling in the cell, weighted by sample area.
        var weight = new double[count];
        var landWeight = new double[count];
        var landSum = new double[count];
        var landSumSq = new double[count];
        var waterSum = new double[count];

        Console.WriteLine("Reading elevation samples...");
        Accumulate(grid, elevationPath, width, height, weight, landWeight, landSum, landSumSq, waterSum);

        for (int cell = 0; cell < count; cell++)
        {
            if (weight[cell] <= 0.0)
            {
                throw new InvalidOperationException($"Cell {cell} received no elevation samples.");
            }
        }

        // 0 = no override, 1 = forced water, 2 = forced land.
        var forced = new byte[count];
        int overrideCells = ApplyOverrides(grid, overridesPath, forced);
        Console.WriteLine($"Overrides touched {overrideCells} cells.");

        // A cell is "low" when most of it is at or below sea level, or it is forced to water.
        var low = new bool[count];
        for (int cell = 0; cell < count; cell++)
        {
            bool mostlyBelowSea = landWeight[cell] < 0.5 * weight[cell];
            low[cell] = forced[cell] == 1 || (forced[cell] == 0 && mostlyBelowSea);
        }

        // Sea is every low cell connected to the open ocean. Low cells that are cut off
        // (dry depressions, and inland seas until lakes arrive in milestone 1) stay land.
        var sea = new bool[count];
        int seed = grid.FindCell(0.0, -30.0);
        if (!low[seed])
        {
            throw new InvalidOperationException("The mid-Atlantic seed cell is not below sea level.");
        }

        var queue = new Queue<int>();
        sea[seed] = true;
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            foreach (int other in grid.Neighbours(cell))
            {
                if (low[other] && !sea[other])
                {
                    sea[other] = true;
                    queue.Enqueue(other);
                }
            }
        }

        var elevation = new short[count];
        var terrain = new TerrainClass[count];
        var classCounts = new int[5];
        double landArea = 0.0;
        double totalArea = 0.0;
        int enclosedLow = 0;

        for (int cell = 0; cell < count; cell++)
        {
            double waterWeight = weight[cell] - landWeight[cell];
            double value;
            TerrainClass kind;

            if (sea[cell])
            {
                value = waterWeight > 0.0 ? waterSum[cell] / waterWeight : -1.0;
                value = Math.Min(value, -1.0);

                kind = TerrainClass.Ocean;
                foreach (int other in grid.Neighbours(cell))
                {
                    if (!sea[other])
                    {
                        kind = TerrainClass.Coast;
                        break;
                    }
                }
            }
            else
            {
                double relief = 0.0;
                if (low[cell])
                {
                    // Enclosed basin below sea level: keep its true mean elevation.
                    enclosedLow++;
                    value = (landSum[cell] + waterSum[cell]) / weight[cell];
                }
                else if (landWeight[cell] > 0.0)
                {
                    value = landSum[cell] / landWeight[cell];
                    double variance = landSumSq[cell] / landWeight[cell] - value * value;
                    relief = Math.Sqrt(Math.Max(0.0, variance));
                }
                else
                {
                    // Forced land with no samples above sea level.
                    value = 1.0;
                }

                if (relief >= MountainsReliefMetres || value >= HighPlateauMetres)
                {
                    kind = TerrainClass.Mountains;
                }
                else if (relief >= HillsReliefMetres)
                {
                    kind = TerrainClass.Hills;
                }
                else
                {
                    kind = TerrainClass.Lowland;
                }

                landArea += grid.AreaKm2(cell);
            }

            totalArea += grid.AreaKm2(cell);
            elevation[cell] = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
            terrain[cell] = kind;
            classCounts[(int)kind]++;
        }

        var world = new WorldMap("Earth", grid, elevation, terrain);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        world.Save(outPath);

        Console.WriteLine($"Wrote {outPath}");
        Console.WriteLine($"Checksum {world.Checksum}");
        Console.WriteLine($"Cells: {count}");
        for (int k = 0; k < classCounts.Length; k++)
        {
            Console.WriteLine($"  {(TerrainClass)k}: {classCounts[k]}");
        }

        Console.WriteLine($"Land share of surface: {100.0 * landArea / totalArea:F2}%");
        Console.WriteLine($"Enclosed below-sea-level cells kept as land: {enclosedLow}");
        PrintProbes(world);

        if (previewPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(previewPath))!);
            Preview.Write(world, previewPath, 2160, 1080);
            Console.WriteLine($"Wrote {previewPath}");
        }

        return 0;
    }

    private static string Require(Dictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out string? value))
        {
            throw new ArgumentException($"Missing option {key}.");
        }

        return value;
    }

    private static void Accumulate(
        GeodesicGrid grid,
        string path,
        int width,
        int height,
        double[] weight,
        double[] landWeight,
        double[] landSum,
        double[] landSumSq,
        double[] waterSum)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The import tool needs a little-endian machine.");
        }

        var cosLon = new double[width];
        var sinLon = new double[width];
        for (int col = 0; col < width; col++)
        {
            double lon = (-180.0 + (col + 0.5) * 360.0 / width) * Math.PI / 180.0;
            cosLon[col] = Math.Cos(lon);
            sinLon[col] = Math.Sin(lon);
        }

        var rowBytes = new byte[width * 2];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        if (stream.Length != (long)width * height * 2)
        {
            throw new InvalidDataException("Elevation file size does not match the given width and height.");
        }

        int rowStartCell = 0;
        for (int row = 0; row < height; row++)
        {
            stream.ReadExactly(rowBytes, 0, rowBytes.Length);
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(rowBytes);

            double lat = (90.0 - (row + 0.5) * 180.0 / height) * Math.PI / 180.0;
            double cosLat = Math.Cos(lat);
            double sinLat = Math.Sin(lat);

            int cell = rowStartCell;
            for (int col = 0; col < width; col++)
            {
                var point = new Vec3(cosLat * cosLon[col], cosLat * sinLon[col], sinLat);
                cell = grid.FindCellFrom(cell, point);
                if (col == 0)
                {
                    rowStartCell = cell;
                }

                double e = samples[col];
                weight[cell] += cosLat;
                if (e > 0.0)
                {
                    landWeight[cell] += cosLat;
                    landSum[cell] += cosLat * e;
                    landSumSq[cell] += cosLat * e * e;
                }
                else
                {
                    waterSum[cell] += cosLat * e;
                }
            }
        }
    }

    // Each line: "water" or "land", then lat1 lon1 lat2 lon2. Every cell along the path is forced.
    private static int ApplyOverrides(GeodesicGrid grid, string path, byte[] forced)
    {
        int touched = 0;
        int lineNumber = 0;
        foreach (string rawLine in File.ReadLines(path))
        {
            lineNumber++;
            string line = rawLine;
            int hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line.Substring(0, hash);
            }

            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (parts.Length != 5 || (parts[0] != "water" && parts[0] != "land"))
            {
                throw new InvalidDataException($"Bad override on line {lineNumber} of {path}.");
            }

            byte value = parts[0] == "water" ? (byte)1 : (byte)2;
            Vec3 a = Vec3.FromLatLon(Parse(parts[1]), Parse(parts[2]));
            Vec3 b = Vec3.FromLatLon(Parse(parts[3]), Parse(parts[4]));
            double km = GeodesicGrid.EarthRadiusKm * Vec3.AngleBetween(a, b);
            int steps = Math.Max(1, (int)Math.Ceiling(km / PathStepKm));
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                Vec3 p = ((1.0 - t) * a + t * b).Normalized();
                int cell = grid.FindCell(p);
                if (forced[cell] != value)
                {
                    forced[cell] = value;
                    touched++;
                }
            }
        }

        return touched;
    }

    private static double Parse(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    private static void PrintProbes(WorldMap world)
    {
        (string Name, double Lat, double Lon)[] probes =
        {
            ("Mid-Pacific", 0.0, -140.0),
            ("Central Sahara", 23.0, 12.0),
            ("Amazon basin", -3.0, -62.0),
            ("Tibet", 33.0, 88.0),
            ("Alps", 46.5, 10.0),
            ("Black Sea", 43.0, 34.0),
            ("Mediterranean", 35.0, 18.0),
            ("Baltic", 58.0, 20.0),
            ("Red Sea", 20.0, 38.5),
            ("Persian Gulf", 27.0, 51.0),
            ("Sea of Marmara", 40.7, 28.2),
            ("Bosporus", 41.1, 29.05),
            ("Strait of Dover", 51.0, 1.5),
            ("Strait of Gibraltar", 35.95, -5.6),
            ("Panama", 9.1, -79.7),
            ("Caspian Sea", 41.0, 51.0),
            ("Lake Superior", 47.7, -87.5),
            ("Greenland interior", 72.0, -40.0),
            ("Antarctica interior", -80.0, 0.0),
            ("Sicily", 37.6, 14.2),
            ("Hokkaido", 43.4, 142.8),
            ("Netherlands", 52.3, 5.3),
        };

        Console.WriteLine("Probe points:");
        foreach (var probe in probes)
        {
            int cell = world.Grid.FindCell(probe.Lat, probe.Lon);
            Console.WriteLine($"  {probe.Name}: {world.Terrain(cell)}, {world.ElevationMetres(cell)} m");
        }
    }
}
