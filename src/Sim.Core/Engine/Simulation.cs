using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Sim.Core.Map;

namespace Sim.Core.Engine;

/// <summary>
/// Advances a world one month at a time. On each tick, systems run in the order they were
/// registered, and a system with interval N runs on ticks divisible by N. Yearly systems
/// therefore run in January and seasonal ones in January, April, July and October.
/// </summary>
public sealed class Simulation
{
    public const int SaveFormatVersion = 2;

    private static readonly byte[] SaveMagic = Encoding.ASCII.GetBytes("SIM2SAVE");

    private readonly ISimSystem[] _systems;
    private readonly SimContext[] _contexts;

    /// <param name="map">The fixed physical map.</param>
    /// <param name="seed">World seed. The same seed gives the same history.</param>
    /// <param name="systems">Systems in their fixed update order.</param>
    public Simulation(WorldMap map, ulong seed, IReadOnlyList<ISimSystem> systems)
    {
        Map = map;
        Seed = seed;
        State = new WorldState(map.CellCount);
        _systems = new ISimSystem[systems.Count];
        _contexts = new SimContext[systems.Count];

        for (int i = 0; i < systems.Count; i++)
        {
            ISimSystem system = systems[i];
            if (string.IsNullOrWhiteSpace(system.Name))
            {
                throw new ArgumentException("Every system needs a name.", nameof(systems));
            }

            if (system.IntervalMonths != 1 && system.IntervalMonths != 3 && system.IntervalMonths != 12)
            {
                throw new ArgumentException(
                    $"System '{system.Name}' has interval {system.IntervalMonths}; it must be 1, 3 or 12 months.",
                    nameof(systems));
            }

            for (int j = 0; j < i; j++)
            {
                if (_systems[j].Name == system.Name)
                {
                    throw new ArgumentException($"Two systems are named '{system.Name}'.", nameof(systems));
                }
            }

            _systems[i] = system;
            _contexts[i] = new SimContext(map, State, Rng.ForStream(seed, "system:" + system.Name));
        }

        foreach (ISimSystem system in _systems)
        {
            system.Register(State);
        }

        State.Seal();

        for (int i = 0; i < _systems.Length; i++)
        {
            _contexts[i].Tick = 0;
            _systems[i].Initialize(_contexts[i]);
        }
    }

    public WorldMap Map { get; }

    public ulong Seed { get; private set; }

    /// <summary>The changing state of the world.</summary>
    public WorldState State { get; }

    /// <summary>The next tick to be simulated. Starts at 0.</summary>
    public long Tick { get; private set; }

    public GameDate Date => new GameDate(Tick);

    public IReadOnlyList<ISimSystem> Systems => _systems;

    /// <summary>Simulates one month.</summary>
    public void Step()
    {
        for (int i = 0; i < _systems.Length; i++)
        {
            if (Tick % _systems[i].IntervalMonths == 0)
            {
                _contexts[i].Tick = Tick;
                _systems[i].Update(_contexts[i]);
            }
        }

        Tick++;
    }

    /// <summary>Simulates the given number of months.</summary>
    public void Run(long ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), "Cannot run backwards.");
        }

        for (long t = 0; t < ticks; t++)
        {
            Step();
        }
    }

    /// <summary>
    /// A fingerprint of the whole run so far: seed, tick, every random stream and all state.
    /// Two runs with the same checksum are in the same state.
    /// </summary>
    public string StateChecksum()
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            WritePayload(writer);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    public void Save(string path)
    {
        using FileStream file = File.Create(path);
        Save(file);
    }

    /// <summary>Writes a compressed save. It refers to the map by checksum and does not contain it.</summary>
    public void Save(Stream stream)
    {
        stream.Write(SaveMagic, 0, SaveMagic.Length);
        stream.Write(BitConverter.GetBytes(SaveFormatVersion), 0, sizeof(int));
        using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true);
        WritePayload(writer);
    }

    public void Load(string path)
    {
        using FileStream file = File.OpenRead(path);
        Load(file);
    }

    /// <summary>
    /// Replaces this simulation's state with a save. The simulation must have been created with
    /// the same map and the same systems. If the save does not fit, nothing is changed.
    /// </summary>
    public void Load(Stream stream)
    {
        var header = new byte[SaveMagic.Length + sizeof(int)];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read != header.Length || !header.AsSpan(0, SaveMagic.Length).SequenceEqual(SaveMagic))
        {
            throw new InvalidDataException("Not a sim2 save file.");
        }

        int version = BitConverter.ToInt32(header, SaveMagic.Length);
        if (version != SaveFormatVersion)
        {
            throw new InvalidDataException($"Save file version {version} is not supported.");
        }

        try
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: true);
            ReadPayload(reader);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The save file is cut short.");
        }
    }

    private void WritePayload(BinaryWriter writer)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Saves need a little-endian machine.");
        }

        writer.Write(Map.Checksum);
        writer.Write(Seed);
        writer.Write(Tick);
        writer.Write(_systems.Length);
        for (int i = 0; i < _systems.Length; i++)
        {
            writer.Write(_systems[i].Name);
            foreach (ulong part in _contexts[i].Rng.GetState())
            {
                writer.Write(part);
            }
        }

        State.Write(writer);
    }

    private void ReadPayload(BinaryReader reader)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Saves need a little-endian machine.");
        }

        string mapChecksum = reader.ReadString();
        if (mapChecksum != Map.Checksum)
        {
            throw new InvalidDataException("This save belongs to a different world map.");
        }

        ulong seed = reader.ReadUInt64();
        long tick = reader.ReadInt64();
        if (tick < 0)
        {
            throw new InvalidDataException("The save file holds an invalid date.");
        }

        int systemCount = reader.ReadInt32();
        if (systemCount != _systems.Length)
        {
            throw new InvalidDataException("The save was made with a different set of systems.");
        }

        var rngStates = new ulong[systemCount][];
        for (int i = 0; i < systemCount; i++)
        {
            string name = reader.ReadString();
            if (name != _systems[i].Name)
            {
                throw new InvalidDataException($"The save expects system '{name}' where this simulation has '{_systems[i].Name}'.");
            }

            var parts = new ulong[Rng.StateSize];
            for (int p = 0; p < parts.Length; p++)
            {
                parts[p] = reader.ReadUInt64();
            }

            rngStates[i] = parts;
        }

        Action applyState = State.Read(reader);

        // Everything checked out: only now change the simulation.
        Seed = seed;
        Tick = tick;
        for (int i = 0; i < systemCount; i++)
        {
            _contexts[i].Rng.SetState(rngStates[i]);
        }

        applyState();
    }
}
