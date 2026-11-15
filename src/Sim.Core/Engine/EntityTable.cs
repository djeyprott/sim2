namespace Sim.Core.Engine;

/// <summary>
/// Saved state for things that are not cells, such as settlements or climate regions: a table
/// whose rows have stable ids. Ids start at 0, are given out in order and are never reused, so an
/// id means the same row for the whole run. Columns are added while systems register; rows can be
/// added and removed at any time. Tables are saved, loaded and checksummed with the rest of the
/// world state.
/// </summary>
public sealed class EntityTable
{
    private const int InitialCapacity = 16;
    private const int MaxRows = 100_000_000;

    private readonly List<TableColumn> _columns = new List<TableColumn>();
    private bool[] _alive = new bool[InitialCapacity];
    private bool _sealed;

    internal EntityTable(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>How many ids have been given out. Every id from 0 to IdCount - 1 exists, alive or removed.</summary>
    public int IdCount { get; private set; }

    /// <summary>How many rows are alive.</summary>
    public int LiveCount { get; private set; }

    public IReadOnlyList<string> ColumnNames => _columns.Select(c => c.Name).ToArray();

    public IntColumn AddIntColumn(string name) => AddColumn(new IntColumn(name, this));

    public DoubleColumn AddDoubleColumn(string name) => AddColumn(new DoubleColumn(name, this));

    public StringColumn AddStringColumn(string name) => AddColumn(new StringColumn(name, this));

    public IntColumn IntColumn(string name) =>
        FindColumn(name) as IntColumn ?? throw new InvalidOperationException($"Column '{name}' of table '{Name}' does not hold whole numbers.");

    public DoubleColumn DoubleColumn(string name) =>
        FindColumn(name) as DoubleColumn ?? throw new InvalidOperationException($"Column '{name}' of table '{Name}' does not hold decimals.");

    public StringColumn StringColumn(string name) =>
        FindColumn(name) as StringColumn ?? throw new InvalidOperationException($"Column '{name}' of table '{Name}' does not hold text.");

    /// <summary>Adds a row with every column at its default value, and returns its id.</summary>
    public int Add()
    {
        int id = IdCount;
        if (id >= MaxRows)
        {
            throw new InvalidOperationException($"Table '{Name}' is full.");
        }

        if (id == _alive.Length)
        {
            Grow(_alive.Length * 2);
        }

        _alive[id] = true;
        IdCount++;
        LiveCount++;
        return id;
    }

    /// <summary>Marks a row as removed. Its id is never given out again.</summary>
    public void Remove(int id)
    {
        CheckId(id);
        if (!_alive[id])
        {
            throw new InvalidOperationException($"Row {id} of table '{Name}' is already removed.");
        }

        _alive[id] = false;
        LiveCount--;
    }

    public bool IsAlive(int id) => (uint)id < (uint)IdCount && _alive[id];

    internal void CheckId(int id)
    {
        if ((uint)id >= (uint)IdCount)
        {
            throw new ArgumentOutOfRangeException(nameof(id), $"Table '{Name}' has no row {id}.");
        }
    }

    internal void Seal() => _sealed = true;

    internal void Write(BinaryWriter writer)
    {
        writer.Write(Name);
        writer.Write(IdCount);
        for (int id = 0; id < IdCount; id++)
        {
            writer.Write(_alive[id]);
        }

        writer.Write(_columns.Count);
        foreach (TableColumn column in _columns)
        {
            writer.Write(column.Name);
            writer.Write(column.TypeCode);
            column.Write(writer, IdCount);
        }
    }

    /// <summary>Reads and checks a saved table without changing anything. Returns the action that applies it.</summary>
    internal Action Read(BinaryReader reader)
    {
        string name = reader.ReadString();
        if (name != Name)
        {
            throw new InvalidDataException($"The save has table '{name}' where this version of the simulation has '{Name}'.");
        }

        int idCount = reader.ReadInt32();
        if (idCount < 0 || idCount > MaxRows)
        {
            throw new InvalidDataException($"Table '{Name}' in the save has an invalid row count.");
        }

        int capacity = InitialCapacity;
        while (capacity < idCount)
        {
            capacity *= 2;
        }

        var alive = new bool[capacity];
        int live = 0;
        for (int id = 0; id < idCount; id++)
        {
            alive[id] = reader.ReadBoolean();
            if (alive[id])
            {
                live++;
            }
        }

        int columnCount = reader.ReadInt32();
        if (columnCount != _columns.Count)
        {
            throw new InvalidDataException($"Table '{Name}' in the save has a different set of columns.");
        }

        var applies = new Action[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            string columnName = reader.ReadString();
            byte type = reader.ReadByte();
            TableColumn column = _columns[i];
            if (columnName != column.Name || type != column.TypeCode)
            {
                throw new InvalidDataException($"Saved column '{columnName}' does not match column '{column.Name}' of table '{Name}'.");
            }

            applies[i] = column.Read(reader, idCount, capacity);
        }

        return () =>
        {
            _alive = alive;
            IdCount = idCount;
            LiveCount = live;
            foreach (Action apply in applies)
            {
                apply();
            }
        };
    }

    private T AddColumn<T>(T column)
        where T : TableColumn
    {
        if (_sealed)
        {
            throw new InvalidOperationException("Columns can only be added while systems are being registered.");
        }

        if (string.IsNullOrWhiteSpace(column.Name))
        {
            throw new ArgumentException("A column needs a name.");
        }

        foreach (TableColumn existing in _columns)
        {
            if (existing.Name == column.Name)
            {
                throw new ArgumentException($"Table '{Name}' already has a column named '{column.Name}'.");
            }
        }

        column.Resize(_alive.Length);
        _columns.Add(column);
        return column;
    }

    private TableColumn FindColumn(string name)
    {
        foreach (TableColumn column in _columns)
        {
            if (column.Name == name)
            {
                return column;
            }
        }

        throw new KeyNotFoundException($"Table '{Name}' has no column named '{name}'.");
    }

    private void Grow(int capacity)
    {
        Array.Resize(ref _alive, capacity);
        foreach (TableColumn column in _columns)
        {
            column.Resize(capacity);
        }
    }
}

/// <summary>One column of an <see cref="EntityTable"/>. Read and write values with the row id as index.</summary>
public abstract class TableColumn
{
    private protected TableColumn(string name, EntityTable table)
    {
        Name = name;
        Table = table;
    }

    public string Name { get; }

    private protected EntityTable Table { get; }

    internal abstract byte TypeCode { get; }

    internal abstract void Resize(int capacity);

    internal abstract void Write(BinaryWriter writer, int count);

    internal abstract Action Read(BinaryReader reader, int count, int capacity);
}

public sealed class IntColumn : TableColumn
{
    private int[] _values = Array.Empty<int>();

    internal IntColumn(string name, EntityTable table)
        : base(name, table)
    {
    }

    public int this[int id]
    {
        get
        {
            Table.CheckId(id);
            return _values[id];
        }

        set
        {
            Table.CheckId(id);
            _values[id] = value;
        }
    }

    internal override byte TypeCode => 0;

    internal override void Resize(int capacity) => Array.Resize(ref _values, capacity);

    internal override void Write(BinaryWriter writer, int count)
    {
        for (int id = 0; id < count; id++)
        {
            writer.Write(_values[id]);
        }
    }

    internal override Action Read(BinaryReader reader, int count, int capacity)
    {
        var values = new int[capacity];
        for (int id = 0; id < count; id++)
        {
            values[id] = reader.ReadInt32();
        }

        return () => _values = values;
    }
}

public sealed class DoubleColumn : TableColumn
{
    private double[] _values = Array.Empty<double>();

    internal DoubleColumn(string name, EntityTable table)
        : base(name, table)
    {
    }

    public double this[int id]
    {
        get
        {
            Table.CheckId(id);
            return _values[id];
        }

        set
        {
            Table.CheckId(id);
            _values[id] = value;
        }
    }

    internal override byte TypeCode => 1;

    internal override void Resize(int capacity) => Array.Resize(ref _values, capacity);

    internal override void Write(BinaryWriter writer, int count)
    {
        for (int id = 0; id < count; id++)
        {
            writer.Write(_values[id]);
        }
    }

    internal override Action Read(BinaryReader reader, int count, int capacity)
    {
        var values = new double[capacity];
        for (int id = 0; id < count; id++)
        {
            values[id] = reader.ReadDouble();
        }

        return () => _values = values;
    }
}

public sealed class StringColumn : TableColumn
{
    private string?[] _values = Array.Empty<string?>();

    internal StringColumn(string name, EntityTable table)
        : base(name, table)
    {
    }

    /// <summary>Text value of a row. Null until set.</summary>
    public string? this[int id]
    {
        get
        {
            Table.CheckId(id);
            return _values[id];
        }

        set
        {
            Table.CheckId(id);
            _values[id] = value;
        }
    }

    internal override byte TypeCode => 2;

    internal override void Resize(int capacity) => Array.Resize(ref _values, capacity);

    internal override void Write(BinaryWriter writer, int count)
    {
        for (int id = 0; id < count; id++)
        {
            string? value = _values[id];
            writer.Write(value != null);
            if (value != null)
            {
                writer.Write(value);
            }
        }
    }

    internal override Action Read(BinaryReader reader, int count, int capacity)
    {
        var values = new string?[capacity];
        for (int id = 0; id < count; id++)
        {
            values[id] = reader.ReadBoolean() ? reader.ReadString() : null;
        }

        return () => _values = values;
    }
}
