using System.Security.Cryptography;
using System.Text;
using Sim.Core.Geo;

namespace Sim.Core.Map;

/// <summary>
/// Coarse physical class of a cell, computed once by the import tool.
/// Detailed, data-driven terrain types arrive with milestone 1.
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
}

/// <summary>
/// The fixed physical map of a world: the grid plus per-cell elevation and terrain class.
/// It never changes during a run. Saves refer to it by <see cref="Checksum"/>.
/// </summary>
public sealed class WorldMap
{
    public const int FormatVersion = 1;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SIM2WRLD");

    private readonly short[] _elevation;
    private readonly TerrainClass[] _terrain;

    public WorldMap(string name, GeodesicGrid grid, short[] elevationMetres, TerrainClass[] terrain)
    {
        if (elevationMetres.Length != grid.CellCount || terrain.Length != grid.CellCount)
        {
            throw new ArgumentException("Per-cell arrays must have one entry per grid cell.");
        }

        Name = name;
        Grid = grid;
        _elevation = elevationMetres;
        _terrain = terrain;
        Checksum = Convert.ToHexString(SHA256.HashData(ToBytes()));
    }

    public string Name { get; }

    public GeodesicGrid Grid { get; }

    /// <summary>SHA-256 of the world file content, as hexadecimal.</summary>
    public string Checksum { get; }

    public int CellCount => Grid.CellCount;

    /// <summary>Mean elevation in metres. Negative for sea cells (depth) and for land below sea level.</summary>
    public short ElevationMetres(int cell) => _elevation[cell];

    public TerrainClass Terrain(int cell) => _terrain[cell];

    public bool IsLand(int cell) => _terrain[cell] >= TerrainClass.Lowland;

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());

    public void Save(Stream stream)
    {
        byte[] bytes = ToBytes();
        stream.Write(bytes, 0, bytes.Length);
    }

    public static WorldMap Load(string path) => FromBytes(File.ReadAllBytes(path), null);

    /// <summary>Loads a world file. Pass an existing grid of the same frequency to avoid rebuilding it.</summary>
    public static WorldMap Load(Stream stream, GeodesicGrid? grid = null)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return FromBytes(buffer.ToArray(), grid);
    }

    private byte[] ToBytes()
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(Grid.Frequency);
            writer.Write(Grid.CellCount);
            writer.Write(Name);
            for (int cell = 0; cell < _elevation.Length; cell++)
            {
                writer.Write(_elevation[cell]);
            }

            for (int cell = 0; cell < _terrain.Length; cell++)
            {
                writer.Write((byte)_terrain[cell]);
            }
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
            throw new InvalidDataException($"World file version {version} is not supported.");
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

        var elevation = new short[cellCount];
        for (int cell = 0; cell < cellCount; cell++)
        {
            elevation[cell] = reader.ReadInt16();
        }

        var terrain = new TerrainClass[cellCount];
        for (int cell = 0; cell < cellCount; cell++)
        {
            byte value = reader.ReadByte();
            if (value > (byte)TerrainClass.Mountains)
            {
                throw new InvalidDataException("World file contains an unknown terrain class.");
            }

            terrain[cell] = (TerrainClass)value;
        }

        return new WorldMap(name, grid, elevation, terrain);
    }
}
