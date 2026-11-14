using System.Globalization;
using System.Runtime.InteropServices;
using Sim.Core.Geo;
using Sim.Core.Map;

namespace Sim.Tools.EarthImport;

/// <summary>
/// Turns global rasters into a world file.
/// Elevation: raw 16-bit little-endian metres. Climate, lakes and rivers: the files written by
/// tools/prepare_environment.py. Every raster has rows from north to south and columns from
/// 180 W to 180 E, each sample at the centre of its pixel.
/// </summary>
public static class Program
{
    // Standard deviation of elevation inside a cell, in metres, at which land counts as hills or mountains.
    private const double HillsReliefMetres = 80.0;
    private const double MountainsReliefMetres = 300.0;

    // Land at or above this mean elevation counts as mountains even when flat (high plateaus).
    private const double HighPlateauMetres = 3000.0;

    // A cell becomes a lake when at least this share of it is covered by lake.
    private const double LakeShare = 0.5;

    private const double PathStepKm = 5.0;
    private const int Months = WorldMap.MonthsPerYear;

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
        string environmentFolder = Require(options, "--environment");
        int envWidth = int.Parse(Require(options, "--env-width"), CultureInfo.InvariantCulture);
        int envHeight = int.Parse(Require(options, "--env-height"), CultureInfo.InvariantCulture);
        string outPath = Require(options, "--out");
        options.TryGetValue("--preview", out string? previewPath);

        Console.WriteLine("Building grid...");
        GeodesicGrid grid = GeodesicGrid.Create(GeodesicGrid.EarthFrequency);
        int count = grid.CellCount;

        // Per-cell sums over the elevation samples falling in the cell, weighted by sample area.
        var weight = new double[count];
        var landWeight = new double[count];
        var landSum = new double[count];
        var landSumSq = new double[count];
        var waterSum = new double[count];

        Console.WriteLine("Reading elevation samples...");
        AccumulateElevation(grid, elevationPath, width, height, weight, landWeight, landSum, landSumSq, waterSum);

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

        // Sea is every low cell connected to the open ocean. Low cells that are cut off stay land
        // unless the lake data below says they are a lake.
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

        Console.WriteLine("Reading climate, lakes and rivers...");
        int[] cellOfSample = MapSamplesToCells(grid, envWidth, envHeight, out double[] sampleWeight);
        int samples = envWidth * envHeight;

        // Lakes.
        byte[] lakeRaster = File.ReadAllBytes(Path.Combine(environmentFolder, "lakes.u8"));
        CheckSize(lakeRaster.Length, samples, "lakes.u8");
        var envWeight = new double[count];
        var lakeWeight = new double[count];
        for (int s = 0; s < samples; s++)
        {
            int cell = cellOfSample[s];
            envWeight[cell] += sampleWeight[s];
            if (lakeRaster[s] != 0)
            {
                lakeWeight[cell] += sampleWeight[s];
            }
        }

        var lake = new bool[count];
        int lakeCells = 0;
        for (int cell = 0; cell < count; cell++)
        {
            if (!sea[cell] && forced[cell] != 2 && envWeight[cell] > 0.0 && lakeWeight[cell] >= LakeShare * envWeight[cell])
            {
                lake[cell] = true;
                lakeCells++;
            }
        }

        // Rivers: the largest river touching the cell.
        byte[] riverRaster = File.ReadAllBytes(Path.Combine(environmentFolder, "rivers.u8"));
        CheckSize(riverRaster.Length, samples, "rivers.u8");
        var data = new WorldMapData("Earth", grid);
        for (int s = 0; s < samples; s++)
        {
            int cell = cellOfSample[s];
            if (riverRaster[s] > (byte)data.River[cell] && riverRaster[s] <= (byte)RiverSize.Major)
            {
                data.River[cell] = (RiverSize)riverRaster[s];
            }
        }

        // Elevation and terrain class.
        var classCounts = new int[6];
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
                    if (!sea[other] && !lake[other])
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

                if (lake[cell])
                {
                    kind = TerrainClass.Lake;
                }
                else if (relief >= MountainsReliefMetres || value >= HighPlateauMetres)
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

                if (!lake[cell])
                {
                    landArea += grid.AreaKm2(cell);
                }
            }

            totalArea += grid.AreaKm2(cell);
            data.ElevationMetres[cell] = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
            data.Terrain[cell] = kind;
            classCounts[(int)kind]++;

            if (kind != TerrainClass.Lowland && kind != TerrainClass.Hills && kind != TerrainClass.Mountains)
            {
                data.River[cell] = RiverSize.None;
            }
        }

        // Climate for land and lake cells.
        FillClimate(grid, data, sea, environmentFolder, cellOfSample, sampleWeight, samples);

        var world = new WorldMap(data);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        world.Save(outPath);

        int minorRiverCells = 0;
        int majorRiverCells = 0;
        for (int cell = 0; cell < count; cell++)
        {
            if (world.River(cell) == RiverSize.Minor)
            {
                minorRiverCells++;
            }
            else if (world.River(cell) == RiverSize.Major)
            {
                majorRiverCells++;
            }
        }

        Console.WriteLine($"Wrote {outPath} ({new FileInfo(outPath).Length} bytes)");
        Console.WriteLine($"Checksum {world.Checksum}");
        Console.WriteLine($"Cells: {count}");
        for (int k = 0; k < classCounts.Length; k++)
        {
            Console.WriteLine($"  {(TerrainClass)k}: {classCounts[k]}");
        }

        Console.WriteLine($"Land share of surface: {100.0 * landArea / totalArea:F2}%");
        Console.WriteLine($"Lake cells: {lakeCells}");
        Console.WriteLine($"River cells: {minorRiverCells} minor, {majorRiverCells} major");
        Console.WriteLine($"Enclosed below-sea-level cells not counted as sea: {enclosedLow}");
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

    private static void CheckSize(long actual, long expected, string name)
    {
        if (actual != expected)
        {
            throw new InvalidDataException($"{name} has {actual} bytes, expected {expected}.");
        }
    }

    // For each raster sample, the cell it falls in and its relative area.
    private static int[] MapSamplesToCells(GeodesicGrid grid, int width, int height, out double[] sampleWeight)
    {
        var cellOfSample = new int[width * height];
        sampleWeight = new double[width * height];

        var cosLon = new double[width];
        var sinLon = new double[width];
        for (int col = 0; col < width; col++)
        {
            double lon = (-180.0 + (col + 0.5) * 360.0 / width) * Math.PI / 180.0;
            cosLon[col] = Math.Cos(lon);
            sinLon[col] = Math.Sin(lon);
        }

        int rowStartCell = 0;
        for (int row = 0; row < height; row++)
        {
            double lat = (90.0 - (row + 0.5) * 180.0 / height) * Math.PI / 180.0;
            double cosLat = Math.Cos(lat);
            double sinLat = Math.Sin(lat);
            int cell = rowStartCell;
            for (int col = 0; col < width; col++)
            {
                cell = grid.FindCellFrom(cell, new Vec3(cosLat * cosLon[col], cosLat * sinLon[col], sinLat));
                if (col == 0)
                {
                    rowStartCell = cell;
                }

                cellOfSample[row * width + col] = cell;
                sampleWeight[row * width + col] = cosLat;
            }
        }

        return cellOfSample;
    }

    private static void FillClimate(
        GeodesicGrid grid,
        WorldMapData data,
        bool[] sea,
        string folder,
        int[] cellOfSample,
        double[] sampleWeight,
        int samples)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The import tool needs a little-endian machine.");
        }

        int count = grid.CellCount;
        var temperature = new double[count * Months];
        var rainfall = new double[count * Months];
        var climateWeight = new double[count];

        byte[] temperatureBytes = File.ReadAllBytes(Path.Combine(folder, "climate_tmean.i16"));
        byte[] rainfallBytes = File.ReadAllBytes(Path.Combine(folder, "climate_ppt.i16"));
        CheckSize(temperatureBytes.Length, (long)samples * Months * 2, "climate_tmean.i16");
        CheckSize(rainfallBytes.Length, (long)samples * Months * 2, "climate_ppt.i16");
        ReadOnlySpan<short> temperatureRaster = MemoryMarshal.Cast<byte, short>(temperatureBytes);
        ReadOnlySpan<short> rainfallRaster = MemoryMarshal.Cast<byte, short>(rainfallBytes);

        for (int s = 0; s < samples; s++)
        {
            // A sample counts only if it has data in every month.
            bool valid = true;
            for (int month = 0; month < Months; month++)
            {
                if (temperatureRaster[month * samples + s] == short.MinValue || rainfallRaster[month * samples + s] == short.MinValue)
                {
                    valid = false;
                    break;
                }
            }

            if (!valid)
            {
                continue;
            }

            int cell = cellOfSample[s];
            double w = sampleWeight[s];
            climateWeight[cell] += w;
            for (int month = 0; month < Months; month++)
            {
                temperature[cell * Months + month] += w * temperatureRaster[month * samples + s] / 10.0;
                rainfall[cell * Months + month] += w * rainfallRaster[month * samples + s];
            }
        }

        var known = new bool[count];
        int direct = 0;
        for (int cell = 0; cell < count; cell++)
        {
            if (!sea[cell] && climateWeight[cell] > 0.0)
            {
                for (int month = 0; month < Months; month++)
                {
                    temperature[cell * Months + month] /= climateWeight[cell];
                    rainfall[cell * Months + month] /= climateWeight[cell];
                }

                known[cell] = true;
                direct++;
            }
        }

        // Cells the dataset misses (small islands, coast slivers) copy the average of known neighbours.
        int filled = 0;
        for (int pass = 0; pass < 6; pass++)
        {
            var newlyKnown = new List<int>();
            for (int cell = 0; cell < count; cell++)
            {
                if (sea[cell] || known[cell])
                {
                    continue;
                }

                int sources = 0;
                foreach (int other in grid.Neighbours(cell))
                {
                    if (known[other])
                    {
                        sources++;
                    }
                }

                if (sources == 0)
                {
                    continue;
                }

                for (int month = 0; month < Months; month++)
                {
                    double t = 0.0;
                    double r = 0.0;
                    foreach (int other in grid.Neighbours(cell))
                    {
                        if (known[other])
                        {
                            t += temperature[other * Months + month];
                            r += rainfall[other * Months + month];
                        }
                    }

                    temperature[cell * Months + month] = t / sources;
                    rainfall[cell * Months + month] = r / sources;
                }

                newlyKnown.Add(cell);
            }

            foreach (int cell in newlyKnown)
            {
                known[cell] = true;
            }

            filled += newlyKnown.Count;
            if (newlyKnown.Count == 0)
            {
                break;
            }
        }

        // Whatever is still unknown (Antarctica, remote islands) gets a simple estimate from
        // latitude and elevation.
        int estimated = 0;
        for (int cell = 0; cell < count; cell++)
        {
            if (sea[cell] || known[cell])
            {
                continue;
            }

            double latitude = grid.Latitude(cell) * Math.PI / 180.0;
            double sinLat = Math.Sin(latitude);
            double elevation = Math.Max(0, (int)data.ElevationMetres[cell]);
            double annual = 28.0 - 50.0 * sinLat * sinLat - 0.0065 * elevation;
            double swing = 15.0 * Math.Abs(sinLat);
            int warmestMonth = latitude >= 0.0 ? 6 : 0;
            for (int month = 0; month < Months; month++)
            {
                temperature[cell * Months + month] = annual + swing * Math.Cos(2.0 * Math.PI * (month - warmestMonth) / Months);
                rainfall[cell * Months + month] = Math.Abs(grid.Latitude(cell)) > 60.0 ? 10.0 : 80.0;
            }

            known[cell] = true;
            estimated++;
        }

        for (int cell = 0; cell < count; cell++)
        {
            if (sea[cell])
            {
                continue;
            }

            for (int month = 0; month < Months; month++)
            {
                int index = cell * Months + month;
                data.TemperatureTenths[index] = (short)Math.Clamp(Math.Round(temperature[index] * 10.0), -900.0, 700.0);
                data.RainfallMm[index] = (short)Math.Clamp(Math.Round(rainfall[index]), 0.0, 30000.0);
            }
        }

        Console.WriteLine($"Climate: {direct} cells from data, {filled} filled from neighbours, {estimated} estimated.");
    }

    private static void AccumulateElevation(
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
            ("Bosporus", 41.1, 29.05),
            ("Panama", 9.1, -79.7),
            ("Caspian Sea", 41.0, 51.0),
            ("Lake Superior", 47.7, -87.5),
            ("Lake Victoria", -1.0, 33.0),
            ("Lake Baikal", 53.5, 108.0),
            ("Aral Sea", 45.0, 60.0),
            ("Greenland interior", 72.0, -40.0),
            ("Antarctica interior", -80.0, 0.0),
            ("Netherlands", 52.3, 5.3),
            ("Nile at Luxor", 25.7, 32.65),
            ("Nile delta", 30.6, 31.2),
            ("Mississippi at Vicksburg", 32.3, -90.9),
            ("Amazon at Manaus", -3.1, -60.0),
            ("Yangtze at Wuhan", 30.6, 114.3),
            ("Danube at Budapest", 47.5, 19.05),
            ("Euphrates", 33.0, 44.2),
            ("London", 51.5, -0.1),
            ("Moscow", 55.75, 37.6),
            ("Singapore", 1.35, 103.8),
            ("Sydney", -33.9, 151.1),
            ("Yakutsk", 62.0, 129.7),
        };

        Console.WriteLine("Probe points:");
        foreach (var probe in probes)
        {
            int cell = world.Grid.FindCell(probe.Lat, probe.Lon);
            string climate = world.HasClimate(cell)
                ? $", Jan {world.TemperatureC(cell, 0):F1} C, Jul {world.TemperatureC(cell, 6):F1} C, mean {world.MeanTemperatureC(cell):F1} C, rain {world.AnnualRainfallMm(cell):F0} mm"
                : "";
            Console.WriteLine($"  {probe.Name}: {world.Terrain(cell)}, {world.ElevationMetres(cell)} m, river {world.River(cell)}{climate}");
        }
    }
}
