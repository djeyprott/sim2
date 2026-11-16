using Sim.Core.Engine;

namespace Sim.Core.People;

/// <summary>
/// The world's population groups, stored in the world state so they are saved and checksummed.
/// A group lives in one cell and has a size, traits (its occupation; later language and religion)
/// and conditions (its food). Each cell's groups form a linked list. Groups in the same cell with
/// the same traits are always one group, and a cell holds at most <see cref="MaxGroupsPerCell"/>.
/// Any system can make one of these from the world state; they all see the same groups.
/// </summary>
public sealed class PopulationGroups
{
    public const string TableName = "people.groups";
    public const string FirstGroupField = "people.firstGroup";
    public const int MaxGroupsPerCell = 8;

    /// <summary>Columns of the groups table named with this prefix hold, per technology, the share of a group's people who know it.</summary>
    public const string KnowledgeColumnPrefix = "know.";

    private readonly EntityTable _table;
    private readonly IntColumn _cell;
    private readonly IntColumn _occupation;
    private readonly IntColumn _next;
    private readonly DoubleColumn _size;
    private readonly DoubleColumn _food;
    private readonly DoubleColumn _everyday;
    private readonly DoubleColumn _luxury;
    private readonly DoubleColumn[] _knowledge;

    // Per cell: the first group's id plus one; 0 means the cell has no groups.
    private readonly int[] _firstPlusOne;

    public PopulationGroups(WorldState state)
    {
        _table = state.Table(TableName);
        _cell = _table.IntColumn("cell");
        _occupation = _table.IntColumn("occupation");
        _next = _table.IntColumn("nextPlusOne");
        _size = _table.DoubleColumn("size");
        _food = _table.DoubleColumn("food");
        _everyday = _table.DoubleColumn("everyday");
        _luxury = _table.DoubleColumn("luxury");
        _knowledge = _table.ColumnNames.Where(n => n.StartsWith(KnowledgeColumnPrefix, StringComparison.Ordinal))
            .Select(n => _table.DoubleColumn(n)).ToArray();
        _firstPlusOne = state.IntField(FirstGroupField);
    }

    /// <summary>Adds a knowledge column for a technology. Called by the technology system while registering.</summary>
    public static DoubleColumn AddKnowledgeColumn(WorldState state, string slug) =>
        state.Table(TableName).AddDoubleColumn(KnowledgeColumnPrefix + slug);

    /// <summary>How many knowledge columns (technologies) the groups carry.</summary>
    public int KnowledgeCount => _knowledge.Length;

    /// <summary>How many group ids have been given out, alive or removed. Ids run from 0 to this minus one.</summary>
    public int IdCount => _table.IdCount;

    /// <summary>Adds the table and the per-cell field. Called once, by <see cref="PopulationSystem"/>.</summary>
    public static void Register(WorldState state)
    {
        EntityTable table = state.AddTable(TableName);
        table.AddIntColumn("cell");
        table.AddIntColumn("occupation");
        table.AddIntColumn("nextPlusOne");
        table.AddDoubleColumn("size");
        table.AddDoubleColumn("food");
        table.AddDoubleColumn("everyday");
        table.AddDoubleColumn("luxury");
        state.AddIntField(FirstGroupField);
    }

    public bool IsAlive(int group) => _table.IsAlive(group);

    public int Cell(int group) => _cell[group];

    public int Occupation(int group) => _occupation[group];

    /// <summary>Number of people in the group.</summary>
    public double Size(int group) => _size[group];

    /// <summary>Share of the group's food needs met this year, 0 to 1.</summary>
    public double Food(int group) => _food[group];

    public void SetSize(int group, double size)
    {
        if (size < 0.0 || double.IsNaN(size) || double.IsInfinity(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size), $"Group {group} cannot have {size} people.");
        }

        _size[group] = size;
    }

    public void SetFood(int group, double food) => _food[group] = Math.Clamp(food, 0.0, 1.0);

    /// <summary>Share of the group's everyday needs (tools, pottery, cloth) met last year, 0 to 1.</summary>
    public double Everyday(int group) => _everyday[group];

    /// <summary>Share of the group's luxury needs (salt, obsidian, ornaments) met last year, 0 to 1.</summary>
    public double Luxury(int group) => _luxury[group];

    public void SetNeedsMet(int group, double everyday, double luxury)
    {
        _everyday[group] = Math.Clamp(everyday, 0.0, 1.0);
        _luxury[group] = Math.Clamp(luxury, 0.0, 1.0);
    }

    /// <summary>The ids of a cell's groups, in list order.</summary>
    public CellGroups InCell(int cell) => new CellGroups(this, _firstPlusOne[cell] - 1);

    public int CountInCell(int cell)
    {
        int count = 0;
        foreach (int _ in InCell(cell))
        {
            count++;
        }

        return count;
    }

    /// <summary>The cell's group with this occupation, or -1.</summary>
    public int Find(int cell, int occupation)
    {
        foreach (int group in InCell(cell))
        {
            if (_occupation[group] == occupation)
            {
                return group;
            }
        }

        return -1;
    }

    /// <summary>
    /// The cell's group with this occupation, created empty (with full food) if missing. If the cell
    /// is full, an empty group is removed to make room, or else its smallest group joins its most
    /// similar group.
    /// </summary>
    public int FindOrAdd(int cell, int occupation)
    {
        int existing = Find(cell, occupation);
        if (existing >= 0)
        {
            return existing;
        }

        if (CountInCell(cell) >= MaxGroupsPerCell)
        {
            MakeRoom(cell);
        }

        int group = _table.Add();
        _cell[group] = cell;
        _occupation[group] = occupation;
        _size[group] = 0.0;
        _food[group] = 1.0;
        _everyday[group] = 0.0;
        _luxury[group] = 0.0;
        foreach (DoubleColumn column in _knowledge)
        {
            column[group] = 0.0;
        }

        // Append to the end of the cell's list, so older groups keep their place.
        int last = -1;
        foreach (int g in InCell(cell))
        {
            last = g;
        }

        if (last < 0)
        {
            _firstPlusOne[cell] = group + 1;
        }
        else
        {
            _next[last] = group + 1;
        }

        return group;
    }

    /// <summary>
    /// Adds people to a group. Its conditions become the size-weighted averages of the two; needs met
    /// that are not given (negative) are taken to be the group's own. The newcomers' knowledge of each
    /// technology is that of the group <paramref name="from"/> they come from, or none if not given.
    /// </summary>
    public void AddPeople(int group, double people, double food, double everyday = -1.0, double luxury = -1.0, int from = -1)
    {
        if (people <= 0.0)
        {
            return;
        }

        double size = _size[group];
        double total = size + people;
        foreach (DoubleColumn column in _knowledge)
        {
            double arriving = from >= 0 ? column[from] : 0.0;
            column[group] = Math.Clamp((column[group] * size + arriving * people) / total, 0.0, 1.0);
        }

        _food[group] = Math.Clamp((_food[group] * size + food * people) / total, 0.0, 1.0);
        if (everyday >= 0.0)
        {
            _everyday[group] = Math.Clamp((_everyday[group] * size + everyday * people) / total, 0.0, 1.0);
        }

        if (luxury >= 0.0)
        {
            _luxury[group] = Math.Clamp((_luxury[group] * size + luxury * people) / total, 0.0, 1.0);
        }

        _size[group] = total;
    }

    /// <summary>Total people in a cell, over all its groups.</summary>
    public double CellTotal(int cell)
    {
        double total = 0.0;
        foreach (int group in InCell(cell))
        {
            total += _size[group];
        }

        return total;
    }

    /// <summary>Total people in the world, optionally only those with one occupation.</summary>
    public double WorldTotal(int occupation = -1)
    {
        double total = 0.0;
        for (int group = 0; group < _table.IdCount; group++)
        {
            if (_table.IsAlive(group) && (occupation < 0 || _occupation[group] == occupation))
            {
                total += _size[group];
            }
        }

        return total;
    }

    /// <summary>Writes each cell's total people into the array, which must have one entry per cell.</summary>
    public void FillCellTotals(double[] totals, int occupation = -1)
    {
        Array.Clear(totals);
        for (int group = 0; group < _table.IdCount; group++)
        {
            if (_table.IsAlive(group) && (occupation < 0 || _occupation[group] == occupation))
            {
                totals[_cell[group]] += _size[group];
            }
        }
    }

    /// <summary>Writes, per cell, the id of its group with this occupation, or -1.</summary>
    public void FillGroupIndex(int[] groupOfCell, int occupation)
    {
        Array.Fill(groupOfCell, -1);
        for (int group = 0; group < _table.IdCount; group++)
        {
            if (_table.IsAlive(group) && _occupation[group] == occupation)
            {
                groupOfCell[_cell[group]] = group;
            }
        }
    }

    // Frees one slot in a full cell: removes an empty group if there is one, or else merges the
    // smallest group into the most similar one (the most shared traits, then the largest).
    private void MakeRoom(int cell)
    {
        int smallest = -1;
        foreach (int group in InCell(cell))
        {
            if (_size[group] <= 0.0)
            {
                Remove(cell, group);
                return;
            }

            if (smallest < 0 || _size[group] < _size[smallest])
            {
                smallest = group;
            }
        }

        int target = -1;
        int bestShared = -1;
        foreach (int group in InCell(cell))
        {
            if (group == smallest)
            {
                continue;
            }

            int shared = SharedTraits(group, smallest);
            if (shared > bestShared || (shared == bestShared && _size[group] > _size[target]))
            {
                target = group;
                bestShared = shared;
            }
        }

        AddPeople(target, _size[smallest], _food[smallest], _everyday[smallest], _luxury[smallest], smallest);
        Remove(cell, smallest);
    }

    private int SharedTraits(int a, int b) => _occupation[a] == _occupation[b] ? 1 : 0;

    private void Remove(int cell, int group)
    {
        int previous = -1;
        foreach (int g in InCell(cell))
        {
            if (g == group)
            {
                break;
            }

            previous = g;
        }

        if (previous < 0)
        {
            _firstPlusOne[cell] = _next[group];
        }
        else
        {
            _next[previous] = _next[group];
        }

        _table.Remove(group);
    }

    /// <summary>Walks a cell's linked list of groups.</summary>
    public readonly struct CellGroups
    {
        private readonly PopulationGroups _groups;
        private readonly int _first;

        internal CellGroups(PopulationGroups groups, int first)
        {
            _groups = groups;
            _first = first;
        }

        public Enumerator GetEnumerator() => new Enumerator(_groups, _first);

        public struct Enumerator
        {
            private readonly PopulationGroups _groups;
            private int _nextGroup;

            internal Enumerator(PopulationGroups groups, int first)
            {
                _groups = groups;
                _nextGroup = first;
                Current = -1;
            }

            public int Current { get; private set; }

            public bool MoveNext()
            {
                if (_nextGroup < 0)
                {
                    return false;
                }

                Current = _nextGroup;
                _nextGroup = _groups._next[_nextGroup] - 1;
                return true;
            }
        }
    }
}

/// <summary>Owns the population groups' storage. Runs first among the people systems; does no work of its own yet.</summary>
public sealed class PopulationSystem : ISimSystem
{
    public const string SystemName = "population";

    public string Name => SystemName;

    public int IntervalMonths => 12;

    public void Register(WorldState state) => PopulationGroups.Register(state);

    public void Update(SimContext context)
    {
    }
}
