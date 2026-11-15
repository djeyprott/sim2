using System.Runtime.InteropServices;

namespace Sim.Core.Engine;

/// <summary>
/// Everything about the world that changes during a run: named arrays with one entry per cell,
/// tables of things that are not cells (<see cref="EntityTable"/>), and text settings of the run.
/// Systems add their fields and tables once, in <see cref="ISimSystem.Register"/>, and keep what
/// they are given. Loading a save fills the same arrays and tables in place.
/// </summary>
public sealed class WorldState
{
    private const byte IntType = 0;
    private const byte DoubleType = 1;

    // A list, not a dictionary, so fields are always saved and hashed in registration order.
    private readonly List<Field> _fields = new List<Field>();
    private readonly List<EntityTable> _tables = new List<EntityTable>();

    // Kept sorted by key (ordinal), so settings are always saved and hashed in the same order.
    private readonly List<KeyValuePair<string, string>> _settings = new List<KeyValuePair<string, string>>();
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

    /// <summary>True if a per-cell field with this name exists, for example because an optional system registered it.</summary>
    public bool HasField(string name) => _fields.Any(f => f.Name == name);

    public int[] IntField(string name) =>
        Find(name).Ints ?? throw new InvalidOperationException($"Field '{name}' does not hold whole numbers.");

    public double[] DoubleField(string name) =>
        Find(name).Doubles ?? throw new InvalidOperationException($"Field '{name}' does not hold decimals.");

    public IReadOnlyList<string> TableNames => _tables.Select(t => t.Name).ToArray();

    /// <summary>The run's settings, sorted by key.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Settings => _settings.ToArray();

    /// <summary>Adds a table of things that are not cells. Only while systems are being registered.</summary>
    public EntityTable AddTable(string name)
    {
        if (_sealed)
        {
            throw new InvalidOperationException("Tables can only be added while systems are being registered.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A table needs a name.");
        }

        foreach (EntityTable existing in _tables)
        {
            if (existing.Name == name)
            {
                throw new ArgumentException($"A table named '{name}' already exists.");
            }
        }

        var table = new EntityTable(name);
        _tables.Add(table);
        return table;
    }

    public EntityTable Table(string name)
    {
        foreach (EntityTable table in _tables)
        {
            if (table.Name == name)
            {
                return table;
            }
        }

        throw new KeyNotFoundException($"There is no table named '{name}'.");
    }

    /// <summary>Sets a text setting of the run, such as a choice made on the start screen.</summary>
    public void SetSetting(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A setting needs a key.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(value);
        int index = FindSetting(key);
        if (index >= 0)
        {
            _settings[index] = new KeyValuePair<string, string>(key, value);
        }
        else
        {
            _settings.Insert(~index, new KeyValuePair<string, string>(key, value));
        }
    }

    /// <summary>A setting's value, or null if it was never set.</summary>
    public string? GetSetting(string key)
    {
        int index = FindSetting(key);
        return index >= 0 ? _settings[index].Value : null;
    }

    internal void Seal()
    {
        _sealed = true;
        foreach (EntityTable table in _tables)
        {
            table.Seal();
        }
    }

    // Index of the key, or the bitwise complement of where it would be inserted.
    private int FindSetting(string key)
    {
        int low = 0;
        int high = _settings.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            int order = string.CompareOrdinal(_settings[middle].Key, key);
            if (order == 0)
            {
                return middle;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return ~low;
    }

    /// <summary>Reads and checks saved state without changing anything. Returns the action that applies it.</summary>
    internal Action Read(BinaryReader reader)
    {
        byte[][] blocks = ReadBlocks(reader);

        int tableCount = reader.ReadInt32();
        if (tableCount != _tables.Count)
        {
            throw new InvalidDataException("The save holds a different set of tables than this version of the simulation.");
        }

        var tableApplies = new Action[tableCount];
        for (int i = 0; i < tableCount; i++)
        {
            tableApplies[i] = _tables[i].Read(reader);
        }

        int settingCount = reader.ReadInt32();
        if (settingCount < 0 || settingCount > 100_000)
        {
            throw new InvalidDataException("The save holds an invalid number of settings.");
        }

        var settings = new List<KeyValuePair<string, string>>(settingCount);
        for (int i = 0; i < settingCount; i++)
        {
            string key = reader.ReadString();
            string value = reader.ReadString();
            if (settings.Count > 0 && string.CompareOrdinal(settings[settings.Count - 1].Key, key) >= 0)
            {
                throw new InvalidDataException("The save's settings are out of order.");
            }

            settings.Add(new KeyValuePair<string, string>(key, value));
        }

        return () =>
        {
            Apply(blocks);
            foreach (Action apply in tableApplies)
            {
                apply();
            }

            _settings.Clear();
            _settings.AddRange(settings);
        };
    }

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

        writer.Write(_tables.Count);
        foreach (EntityTable table in _tables)
        {
            table.Write(writer);
        }

        writer.Write(_settings.Count);
        foreach (KeyValuePair<string, string> setting in _settings)
        {
            writer.Write(setting.Key);
            writer.Write(setting.Value);
        }
    }

    /// <summary>
    /// Reads and checks saved fields without changing anything. Call <see cref="Apply"/> with
    /// the result to copy the values in.
    /// </summary>
    private byte[][] ReadBlocks(BinaryReader reader)
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

    private void Apply(byte[][] blocks)
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
