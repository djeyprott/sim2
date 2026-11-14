using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Sim.Core.Geo;

namespace Sim.Core.Map;

/// <summary>
/// Coarse physical class of a cell, computed once by the import tool.
/// Biomes, which depend on the changing climate, are separate and live in the world state.
/// </summary>
public enum TerrainClass : byte
{
    /// <summary>Sea with no land next to it.</summary>
    Ocean = 0,

    /// <summary>Sea next to at least one land cell.</summary>
    Coast = 1,

    Lowland = 2,
    Hills = 3,
    Mountains = 4,

    /// <summary>Inland water: a lake or an inland sea.</summary>
    Lake = 5,
}

/// <summary>Size of the largest river crossing a cell.</summary>
public enum RiverSize : byte
{
    None = 0,
    Minor = 1,
    Major = 2,
}

/// <summary>The arrays a <see cref="WorldMap"/> is built from. One entry per cell unless stated.</summary>
public sealed class WorldMapData
{
    public WorldMapData(string name, GeodesicGrid grid)
    {
        Name = name;
        Grid = grid;
        int count = grid.CellCount;
        ElevationMetres = new short[count];
        Terrain = new TerrainClass[count];
        River = new RiverSize[count];
        TemperatureTenths = new short[count * WorldMap.MonthsPerYear];
        RainfallMm = new short[count * WorldMap.MonthsPerYear];
        Array.Fill(TemperatureTenths, WorldMap.NoClimate);
        Array.Fill(RainfallMm, WorldMap.NoClimate);
    }

    public string Name { get; }

    public GeodesicGrid Grid { get; }

    public short[] ElevationMetres { get; }

    public TerrainClass[] Terrain { get; }

    public RiverSize[] River { get; }

    /// <summary>Baseline mean temperature in tenths of a degree C, 12 values per cell, January first.</summary>
    public short[] TemperatureTenths { get; }

    /// <summary>Baseline rainfall in mm, 12 values per cell, January first.</summary>
    public short[] RainfallMm { get; }
}

/// <summary>
/// The fixed physical map of a world: the grid plus per-cell elevation, terrain class, rivers
/// and baseline climate. It never changes during a run. Saves refer to it by <see cref="Checksum"/>.
/// </summary>
public sealed class WorldMap
{
    public const int FormatVersion = 2;
    public const int MonthsPerYear = 12;

    /// <summary>Stored in the climate arrays where a cell has no climate data (the sea).</summary>
    public const short NoClimate = short.MinValue;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SIM2WRLD");

    private readonly short[] _elevation;
    private readonly TerrainClass[] _terrain;
    private readonly RiverSize[] _river;
    private readonly short[] _temperature;
    private readonly short[] _rainfall;

    public WorldMap(WorldMapData data)
    {
        int count = data.Grid.CellCount;
        if (data.ElevationMetres.Length != count || data.Terrain.Length != count || data.River.Length != count
            || data.TemperatureTenths.Length != count * MonthsPerYear || data.RainfallMm.Length != count * MonthsPerYear)
        {
            throw new ArgumentException("Per-cell arrays do not match the grid.");
        }

        Name = data.Name;
        Grid = data.Grid;
        _elevation = data.ElevationMetres;
        _terrain = data.Terrain;
        _river = data.River;
        _temperature = data.TemperatureTenths;
        _rainfall = data.RainfallMm;
        Checksum = Convert.ToHexString(SHA256.HashData(ToBytes()));
    }

    public string Name { get; }

    public GeodesicGrid Grid { get; }

    /// <summary>SHA-256 of the world's content, as hexadecimal.</summary>
    public string Checksum { get; }

    public int CellCount => Grid.CellCount;

    /// <summary>Mean elevation in metres. Negative for sea cells (depth) and for land below sea level.</summary>
    public short ElevationMetres(int cell) => _elevation[cell];

    public TerrainClass Terrain(int cell) => _terrain[cell];

    /// <summary>True for lowland, hills and mountains. False for sea and lakes.</summary>
    public bool IsLand(int cell)
    {
        TerrainClass terrain = _terrain[cell];
        return terrain == TerrainClass.Lowland || terrain == TerrainClass.Hills || terrain == TerrainClass.Mountains;
    }

    /// <summary>True for ocean and coast cells.</summary>
    public bool IsSea(int cell) => _terrain[cell] == TerrainClass.Ocean || _terrain[cell] == TerrainClass.Coast;

    public bool IsLake(int cell) => _terrain[cell] == TerrainClass.Lake;

    public RiverSize River(int cell) => _river[cell];

    /// <summary>True when the cell has baseline climate. Land and lakes do; the sea does not.</summary>
    public bool HasClimate(int cell) => _temperature[cell * MonthsPerYear] != NoClimate;

    /// <summary>Baseline mean temperature in degrees C for a month (0 is January).</summary>
    public double TemperatureC(int cell, int month) => _temperature[cell * MonthsPerYear + month] / 10.0;

    /// <summary>Baseline rainfall in mm for a month (0 is January).</summary>
    public double RainfallMm(int cell, int month) => _rainfall[cell * MonthsPerYear + month];

    /// <summary>Baseline mean temperature over the year, in degrees C.</summary>
    public double MeanTemperatureC(int cell)
    {
        double sum = 0.0;
        for (int month = 0; month < MonthsPerYear; month++)
        {
            sum += TemperatureC(cell, month);
        }

        return sum / MonthsPerYear;
    }

    /// <summary>Baseline rainfall over the year, in mm.</summary>
    public double AnnualRainfallMm(int cell)
    {
        double sum = 0.0;
        for (int month = 0; month < MonthsPerYear; month++)
        {
            sum += RainfallMm(cell, month);
        }

        return sum;
    }

    public void Save(string path)
    {
        using FileStream file = File.Create(path);
        Save(file);
    }

    /// <summary>Writes the world, compressed.</summary>
    public void Save(Stream stream)
    {
        byte[] bytes = ToBytes();
        using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        gzip.Write(bytes, 0, bytes.Length);
    }

    public static WorldMap Load(string path)
    {
        using FileStream file = File.OpenRead(path);
        return Load(file);
    }

    /// <summary>Loads a world file. Pass an existing grid of the same frequency to avoid rebuilding it.</summary>
    public static WorldMap Load(Stream stream, GeodesicGrid? grid = null)
    {
        using var compressed = new MemoryStream();
        stream.CopyTo(compressed);
        byte[] raw = compressed.ToArray();

        // World files are gzip-compressed, which always starts with these two bytes.
        if (raw.Length < 2 || raw[0] != 0x1F || raw[1] != 0x8B)
        {
            throw new InvalidDataException("Not a sim2 world file, or one made by an older version.");
        }

        using var buffer = new MemoryStream();
        try
        {
            using var gzip = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
            gzip.CopyTo(buffer);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("The world file is damaged.");
        }

        try
        {
            return FromBytes(buffer.ToArray(), grid);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The world file is cut short.");
        }
    }

    private byte[] ToBytes()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("World files need a little-endian machine.");
        }

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(Grid.Frequency);
            writer.Write(Grid.CellCount);
            writer.Write(Name);
            writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<short>(_elevation)));
            writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<TerrainClass>(_terrain)));
            writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<RiverSize>(_river)));
            writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<short>(_temperature)));
            writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<short>(_rainfall)));
        }

        return buffer.ToArray();
    }

    private static WorldMap FromBytes(byte[] bytes, GeodesicGrid? grid)
    {
        using var buffer = new MemoryStream(bytes);
        using var reader = new BinaryReader(buffer, Encoding.UTF8);

        byte[] magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a sim2 world file.");
        }

        int version = reader.ReadInt32();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"World file version {version} is not supported; this program needs version {FormatVersion}.");
        }

        int frequency = reader.ReadInt32();
        int cellCount = reader.ReadInt32();
        string name = reader.ReadString();

        if (grid == null)
        {
            grid = GeodesicGrid.Create(frequency);
        }
        else if (grid.Frequency != frequency)
        {
            throw new ArgumentException("The supplied grid does not match the world file.", nameof(grid));
        }

        if (cellCount != grid.CellCount)
        {
            throw new InvalidDataException("World file cell count does not match its grid.");
        }

        var data = new WorldMapData(name, grid);
        ReadInto(reader, MemoryMarshal.AsBytes(new Span<short>(data.ElevationMetres)));
        ReadInto(reader, MemoryMarshal.AsBytes(new Span<TerrainClass>(data.Terrain)));
        ReadInto(reader, MemoryMarshal.AsBytes(new Span<RiverSize>(data.River)));
        ReadInto(reader, MemoryMarshal.AsBytes(new Span<short>(data.TemperatureTenths)));
        ReadInto(reader, MemoryMarshal.AsBytes(new Span<short>(data.RainfallMm)));

        for (int cell = 0; cell < cellCount; cell++)
        {
            if ((byte)data.Terrain[cell] > (byte)TerrainClass.Lake || (byte)data.River[cell] > (byte)RiverSize.Major)
            {
                throw new InvalidDataException("World file contains an unknown terrain class or river size.");
            }
        }

        return new WorldMap(data);
    }

    private static void ReadInto(BinaryReader reader, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = reader.Read(destination.Slice(total));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            total += read;
        }
    }
}
