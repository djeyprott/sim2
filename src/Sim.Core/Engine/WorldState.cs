using System.Runtime.InteropServices;

namespace Sim.Core.Engine;

/// <summary>
/// Everything about the world that changes during a run, held as named arrays with one
/// entry per cell. Systems add their fields once, in <see cref="ISimSystem.Register"/>,
/// and keep the returned arrays. Loading a save fills the same arrays in place.
/// </summary>
public sealed class WorldState
{
    private const byte IntType = 0;
    private const byte DoubleType = 1;

    // A list, not a dictionary, so fields are always saved and hashed in registration order.
    private readonly List<Field> _fields = new List<Field>();
    private bool _sealed;

    internal WorldState(int cellCount)
    {
        CellCount = cellCount;
    }

    public int CellCount { get; }

    public IReadOnlyList<string> FieldNames => _fields.Select(f => f.Name).ToArray();

    /// <summary>Adds a whole-number value per cell, starting at zero.</summary>
    public int[] AddIntField(string name)
    {
        var values = new int[CellCount];
        Add(new Field(name, values, null));
        return values;
    }

    /// <summary>Adds a decimal value per cell, starting at zero.</summary>
    public double[] AddDoubleField(string name)
    {
        var values = new double[CellCount];
        Add(new Field(name, null, values));
        return values;
    }

    public int[] IntField(string name) =>
        Find(name).Ints ?? throw new InvalidOperationException($"Field '{name}' does not hold whole numbers.");

    public double[] DoubleField(string name) =>
        Find(name).Doubles ?? throw new InvalidOperationException($"Field '{name}' does not hold decimals.");

    internal void Seal() => _sealed = true;

    internal void Write(BinaryWriter writer)
    {
        writer.Write(_fields.Count);
        foreach (Field field in _fields)
        {
            writer.Write(field.Name);
            if (field.Ints != null)
            {
                writer.Write(IntType);
                writer.Write(field.Ints.Length);
                writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(field.Ints)));
            }
            else
            {
                writer.Write(DoubleType);
                writer.Write(field.Doubles!.Length);
                writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<double>(field.Doubles)));
            }
        }
    }

    /// <summary>
    /// Reads and checks saved fields without changing anything. Call <see cref="Apply"/> with
    /// the result to copy the values in.
    /// </summary>
    internal byte[][] ReadBlocks(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count != _fields.Count)
        {
            throw new InvalidDataException("The save holds a different set of state fields than this version of the simulation.");
        }

        var blocks = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            Field field = _fields[i];
            string name = reader.ReadString();
            byte type = reader.ReadByte();
            int length = reader.ReadInt32();
            byte expectedType = field.Ints != null ? IntType : DoubleType;
            if (name != field.Name || type != expectedType || length != CellCount)
            {
                throw new InvalidDataException($"Saved field '{name}' does not match field '{field.Name}' in this version of the simulation.");
            }

            int byteCount = length * (type == IntType ? sizeof(int) : sizeof(double));
            byte[] block = reader.ReadBytes(byteCount);
            if (block.Length != byteCount)
            {
                throw new InvalidDataException("The save file is cut short.");
            }

            blocks[i] = block;
        }

        return blocks;
    }

    internal void Apply(byte[][] blocks)
    {
        for (int i = 0; i < _fields.Count; i++)
        {
            Field field = _fields[i];
            Array target = field.Ints != null ? (Array)field.Ints : field.Doubles!;
            Buffer.BlockCopy(blocks[i], 0, target, 0, blocks[i].Length);
        }
    }

    private void Add(Field field)
    {
        if (_sealed)
        {
            throw new InvalidOperationException("Fields can only be added while systems are being registered.");
        }

        if (string.IsNullOrWhiteSpace(field.Name))
        {
            throw new ArgumentException("A field needs a name.");
        }

        foreach (Field existing in _fields)
        {
            if (existing.Name == field.Name)
            {
                throw new ArgumentException($"A field named '{field.Name}' already exists.");
            }
        }

        _fields.Add(field);
    }

    private Field Find(string name)
    {
        foreach (Field field in _fields)
        {
            if (field.Name == name)
            {
                return field;
            }
        }

        throw new KeyNotFoundException($"There is no field named '{name}'.");
    }

    private sealed class Field
    {
        public Field(string name, int[]? ints, double[]? doubles)
        {
            Name = name;
            Ints = ints;
            Doubles = doubles;
        }

        public string Name { get; }

        public int[]? Ints { get; }

        public double[]? Doubles { get; }
    }
}
