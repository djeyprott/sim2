using Sim.Core.Engine;
using Sim.Core.Farming;
using Sim.Core.Goods;
using Sim.Core.Map;
using Sim.Core.People;
using Sim.Core.Polities;
using Sim.Core.Settlements;

namespace Sim.Core.Tests;

/// <summary>What the milestone 4 exit tests check about polities in one long run.</summary>
public sealed class PolityResult
{
    /// <summary>Year (from the start) of the first chiefdom and first state, or -1.</summary>
    public int FirstChiefdomYear { get; set; } = -1;

    public int FirstStateYear { get; set; } = -1;

    /// <summary>Where the first state's capital was, and whether that was a river valley or plain.</summary>
    public string FirstStatePlace { get; set; } = "";

    public bool FirstStateInRiverValleyOrPlain { get; set; }

    /// <summary>The farthest apart two polities founded on their own (not successors) were when they became states.</summary>
    public double StateRegionsKm { get; set; }

    /// <summary>Polities whose capital had no farming, or no farmers, when first seen.</summary>
    public int FormedWithoutFarming { get; set; }

    /// <summary>The largest polity at the end: its area and its share of the world's people.</summary>
    public double LargestKm2 { get; set; }

    public double LargestPeopleShare { get; set; }

    public int Collapses { get; set; }

    public int Conquests { get; set; }

    public int Wars { get; set; }

    public int StatesEver { get; set; }

    /// <summary>Median years a polity stayed a state (until it fell back, ended, or the run ended).</summary>
    public double MedianStateYears { get; set; }

    /// <summary>Share of cells ruled by polities at peace that changed hands in a year, the most in any year after the first state.</summary>
    public double MaxPeacefulChange { get; set; }

    public double MeanPeacefulChange { get; set; }

    /// <summary>Wars decided with a winner, and of those how many the stronger side (at the start) won.</summary>
    public int DecidedWars { get; set; }

    public int StrongerWon { get; set; }

    /// <summary>Wars where only one side used chariots (fortifications), and how many that side won.</summary>
    public int ChariotWars { get; set; }

    public int ChariotWins { get; set; }

    public int FortWars { get; set; }

    public int FortWins { get; set; }

    public double WarDeaths { get; set; }

    /// <summary>Growth of the people in cells a polity ruled all year, pooled over polity-years at war and at peace.</summary>
    public double WarStart { get; set; }

    public double WarEnd { get; set; }

    public double PeaceStart { get; set; }

    public double PeaceEnd { get; set; }

    public double WarGrowth => WarStart > 0.0 ? WarEnd / WarStart : double.NaN;

    public double PeaceGrowth => PeaceStart > 0.0 ? PeaceEnd / PeaceStart : double.NaN;

    /// <summary>Per polity that stood at least 50 years: aggression, years standing, wars started.</summary>
    public List<(double Aggression, int Years, int Started)> Temperaments { get; } = new List<(double, int, int)>();

    /// <summary>Per polity-year: the mean unrest of its people, and whether it split that year.</summary>
    public List<(double Unrest, bool Split)> UnrestYears { get; } = new List<(double, bool)>();

    /// <summary>Grain per person in capitals' stores and in other ruled settlements' stores at the end.</summary>
    public double CapitalGrainPerPerson { get; set; }

    public double OtherGrainPerPerson { get; set; }

    public int EliteOutsideCapitals { get; set; }

    public override string ToString() =>
        $"polities: first chiefdom year {FirstChiefdomYear}, first state year {FirstStateYear} at {FirstStatePlace} (river valley or plain {FirstStateInRiverValleyOrPlain}), "
        + $"states up to {StateRegionsKm:F0} km apart, {FormedWithoutFarming} formed without farming; largest {LargestKm2 / 1e6:F2} M km2, {100.0 * LargestPeopleShare:F1}% of people; "
        + $"{StatesEver} states, median {MedianStateYears:F0} years as a state; {Collapses} collapses, {Conquests} conquests, {Wars} wars, {WarDeaths / 1e3:F0} k war deaths; "
        + $"peaceful border change {100.0 * MeanPeacefulChange:F2}% a year (at most {100.0 * MaxPeacefulChange:F2}%); "
        + $"stronger side won {StrongerWon} of {DecidedWars}; chariots won {ChariotWins} of {ChariotWars}, fortifications {FortWins} of {FortWars}; "
        + $"growth at war {WarGrowth:F4}, at peace {PeaceGrowth:F4}; capitals hold {CapitalGrainPerPerson:F2} grain a person, others {OtherGrainPerPerson:F2}; "
        + $"{EliteOutsideCapitals} elite groups outside capitals";
}

/// <summary>Follows the polities through one long run, a year at a time.</summary>
public sealed class PolityMeasures
{
    private readonly Simulation _sim;
    private readonly PolitySystem _polities;
    private readonly PopulationGroups _groups;
    private readonly int[] _land;
    private readonly int[] _cropPlusOne;
    private readonly int _farmer;
    private readonly int _elite;
    private int[] _rulerBefore;
    private readonly double[] _peopleBefore;
    private readonly double[] _people;
    private readonly HashSet<int> _seen = new HashSet<int>();
    private readonly Dictionary<int, int> _years = new Dictionary<int, int>();
    private readonly Dictionary<int, int> _splitsBefore = new Dictionary<int, int>();
    private readonly Dictionary<int, (bool StrongerIsAttacker, bool Even, bool ChariotsA, bool ChariotsD, bool FortsA, bool FortsD)> _warStarts =
        new Dictionary<int, (bool, bool, bool, bool, bool, bool)>();
    private readonly List<double> _peacefulChanges = new List<double>();
    private readonly HashSet<int> _atWarThisYear = new HashSet<int>();

    public PolityMeasures(Simulation sim, int[] land)
    {
        _sim = sim;
        _polities = sim.Systems.OfType<PolitySystem>().Single();
        _groups = new PopulationGroups(sim.State);
        _land = land;
        _cropPlusOne = sim.State.IntField(FarmingSystem.CropField);
        var occupations = OccupationSet.LoadDefault();
        _farmer = occupations.IndexOf(OccupationSet.Farmer);
        _elite = occupations.IndexOf(OccupationSet.Elite);
        _rulerBefore = new int[sim.Map.CellCount];
        Array.Fill(_rulerBefore, -1);
        _peopleBefore = new double[sim.Map.CellCount];
        _people = new double[sim.Map.CellCount];
    }

    public PolityResult Result { get; } = new PolityResult();

    /// <summary>Call after each simulated year.</summary>
    public void Year(int year)
    {
        _groups.FillCellTotals(_people);
        var ruler = new int[_sim.Map.CellCount];
        Array.Fill(ruler, -1);
        foreach (int cell in _land)
        {
            ruler[cell] = _polities.RulerOf(cell);
        }

        // Polities at war at any time this year: wars under way now or ended this year.
        _atWarThisYear.Clear();
        long yearStart = _sim.Tick - GameDate.MonthsPerYear;
        foreach (WarInfo w in _polities.Wars())
        {
            if (w.UnderWay || w.Ended >= yearStart)
            {
                _atWarThisYear.Add(w.Attacker);
                _atWarThisYear.Add(w.Defender);
            }

            if (!_warStarts.ContainsKey(w.Id))
            {
                double a = _polities.IsStanding(w.Attacker) ? _polities.Strength(w.Attacker) : 0.0;
                double d = _polities.IsStanding(w.Defender) ? _polities.Strength(w.Defender) : 0.0;
                bool chariotsA = _polities.IsStanding(w.Attacker) && _polities.AttackFactor(w.Attacker) > 1.0;
                bool chariotsD = _polities.IsStanding(w.Defender) && _polities.AttackFactor(w.Defender) > 1.0;
                bool fortsA = _polities.IsStanding(w.Attacker) && _polities.DefenceFactor(_polities.Info(w.Attacker).Capital) > 1.0;
                bool fortsD = _polities.IsStanding(w.Defender) && _polities.DefenceFactor(_polities.Info(w.Defender).Capital) > 1.0;
                _warStarts[w.Id] = (a > d, a == d, chariotsA, chariotsD, fortsA, fortsD);
            }
        }

        // Cells ruled a year ago by polities at peace all year: the share that changed hands.
        int peaceful = 0, changed = 0;
        foreach (int cell in _land)
        {
            int before = _rulerBefore[cell];
            if (before >= 0 && !_atWarThisYear.Contains(before))
            {
                peaceful++;
                changed += ruler[cell] != before ? 1 : 0;
            }
        }

        if (Result.FirstStateYear >= 0 && peaceful > 0)
        {
            _peacefulChanges.Add(changed / (double)peaceful);
        }

        // Growth of the people in each polity's cells held all year, at war and at peace.
        var start = new Dictionary<int, double>();
        var end = new Dictionary<int, double>();
        foreach (int cell in _land)
        {
            int p = ruler[cell];
            if (p >= 0 && p == _rulerBefore[cell])
            {
                start[p] = start.GetValueOrDefault(p) + _peopleBefore[cell];
                end[p] = end.GetValueOrDefault(p) + _people[cell];
            }
        }

        foreach (int p in start.Keys)
        {
            if (_atWarThisYear.Contains(p))
            {
                Result.WarStart += start[p];
                Result.WarEnd += end[p];
            }
            else
            {
                Result.PeaceStart += start[p];
                Result.PeaceEnd += end[p];
            }
        }

        // Each standing polity: farming at its capital when first seen; years standing; unrest and splits.
        var unrest = new Dictionary<int, (double People, double Unrest)>();
        for (int g = 0; g < _groups.IdCount; g++)
        {
            if (!_groups.IsAlive(g) || _groups.Size(g) <= 0.0)
            {
                continue;
            }

            int p = ruler[_groups.Cell(g)];
            if (p >= 0)
            {
                var u = unrest.GetValueOrDefault(p);
                unrest[p] = (u.People + _groups.Size(g), u.Unrest + _groups.Size(g) * _groups.Unrest(g));
            }
        }

        foreach (PolityInfo p in _polities.Standing())
        {
            if (_seen.Add(p.Id) && (_cropPlusOne[p.Capital] <= 0 || _groups.Find(p.Capital, _farmer) < 0))
            {
                Result.FormedWithoutFarming++;
            }

            _years[p.Id] = _years.GetValueOrDefault(p.Id) + 1;
            int splits = _polities.Splits(p.Id);
            bool split = splits > _splitsBefore.GetValueOrDefault(p.Id);
            _splitsBefore[p.Id] = splits;
            var u = unrest.GetValueOrDefault(p.Id);
            if (u.People > 0.0)
            {
                Result.UnrestYears.Add((u.Unrest / u.People, split));
            }
        }

        if (Result.FirstStateYear < 0 && _polities.Standing().Any(p => p.Level == PolityLevel.State))
        {
            Result.FirstStateYear = year;
        }

        _rulerBefore = ruler;
        Array.Copy(_people, _peopleBefore, _people.Length);
    }

    /// <summary>Call once at the end of the run.</summary>
    public void Finish(int endYear)
    {
        WorldMap map = _sim.Map;
        List<PolityEvent> events = _polities.Events();
        List<PolityInfo> all = _polities.All();
        PolityResult r = Result;

        PolityEvent? firstChiefdom = events.Where(e => e.Kind == PolityEventKind.Founded).Cast<PolityEvent?>().FirstOrDefault();
        r.FirstChiefdomYear = firstChiefdom.HasValue ? (int)(firstChiefdom.Value.Tick / GameDate.MonthsPerYear) : -1;
        PolityEvent? firstState = events.Where(e => e.Kind == PolityEventKind.BecameState).Cast<PolityEvent?>().FirstOrDefault();
        if (firstState.HasValue)
        {
            int cell = firstState.Value.Cell;
            r.FirstStateYear = (int)(firstState.Value.Tick / GameDate.MonthsPerYear);
            r.FirstStatePlace = $"{map.Grid.Latitude(cell):F1},{map.Grid.Longitude(cell):F1}";

            // A river valley or plain: lowland or coast, with a river in the cell or next to it.
            TerrainClass terrain = map.Terrain(cell);
            bool river = map.River(cell) != RiverSize.None;
            foreach (int other in map.Grid.Neighbours(cell))
            {
                river |= map.River(other) != RiverSize.None;
            }

            r.FirstStateInRiverValleyOrPlain = (terrain == TerrainClass.Lowland || terrain == TerrainClass.Coast) && river;
        }

        // States founded on their own: how far apart their capitals were.
        var stateCapitals = events.Where(e => e.Kind == PolityEventKind.BecameState && !_polities.Info(e.Polity).Successor)
            .Select(e => e.Cell).Distinct().ToList();
        for (int i = 0; i < stateCapitals.Count; i++)
        {
            for (int j = i + 1; j < stateCapitals.Count; j++)
            {
                r.StateRegionsKm = Math.Max(r.StateRegionsKm, map.Grid.DistanceKm(stateCapitals[i], stateCapitals[j]));
            }
        }

        // The largest polity now.
        double world = _groups.WorldTotal();
        foreach (PolityInfo p in _polities.Standing())
        {
            double km2 = 0.0, people = 0.0;
            foreach (int cell in _land)
            {
                if (_polities.RulerOf(cell) == p.Id)
                {
                    km2 += map.Grid.AreaKm2(cell);
                    people += _people[cell];
                }
            }

            if (people / world > r.LargestPeopleShare)
            {
                r.LargestPeopleShare = people / world;
                r.LargestKm2 = km2;
            }
        }

        r.Collapses = events.Count(e => e.Kind == PolityEventKind.Split || e.Kind == PolityEventKind.Fell);
        r.Conquests = events.Count(e => e.Kind == PolityEventKind.Conquered);
        List<WarInfo> wars = _polities.Wars();
        r.Wars = wars.Count;
        r.WarDeaths = wars.Sum(w => w.AttackerDeaths + w.DefenderDeaths);

        // Years as a state: from becoming one until falling back, ending, or the end of the run.
        var since = new Dictionary<int, long>();
        var spans = new List<double>();
        foreach (PolityEvent e in events)
        {
            if (e.Kind == PolityEventKind.BecameState)
            {
                since[e.Polity] = e.Tick;
            }
            else if ((e.Kind == PolityEventKind.BecameChiefdom || e.Kind == PolityEventKind.Conquered || e.Kind == PolityEventKind.Fell)
                && since.Remove(e.Polity, out long from))
            {
                spans.Add((e.Tick - from) / (double)GameDate.MonthsPerYear);
            }
        }

        spans.AddRange(since.Values.Select(from => (_sim.Tick - from) / (double)GameDate.MonthsPerYear));
        r.StatesEver = events.Where(e => e.Kind == PolityEventKind.BecameState).Select(e => e.Polity).Distinct().Count();
        spans.Sort();
        r.MedianStateYears = spans.Count > 0 ? spans[spans.Count / 2] : double.NaN;

        r.MeanPeacefulChange = _peacefulChanges.Count > 0 ? _peacefulChanges.Average() : 0.0;
        r.MaxPeacefulChange = _peacefulChanges.Count > 0 ? _peacefulChanges.Max() : 0.0;

        // Who won the wars that were decided.
        foreach (WarInfo w in wars.Where(w => !w.UnderWay && w.Winner >= 0))
        {
            if (!_warStarts.TryGetValue(w.Id, out var start))
            {
                continue;
            }

            bool attackerWon = w.Winner == w.Attacker;
            if (!start.Even)
            {
                r.DecidedWars++;
                r.StrongerWon += attackerWon == start.StrongerIsAttacker ? 1 : 0;
            }

            if (start.ChariotsA != start.ChariotsD)
            {
                r.ChariotWars++;
                r.ChariotWins += attackerWon == start.ChariotsA ? 1 : 0;
            }

            if (start.FortsA != start.FortsD)
            {
                r.FortWars++;
                r.FortWins += attackerWon == start.FortsA ? 1 : 0;
            }
        }

        // Temperament against wars started, for polities that stood at least 50 years.
        foreach (PolityInfo p in all)
        {
            int years = _years.GetValueOrDefault(p.Id);
            if (years >= 50)
            {
                r.Temperaments.Add((p.Aggression, years, wars.Count(w => w.Attacker == p.Id)));
            }
        }

        // Tribute: grain per person in capitals and other ruled settlements; elite only in capitals.
        var stockpiles = new Stockpiles(_sim.State, GoodSet.LoadDefault());
        int grain = stockpiles.Goods.IndexOf(GoodSet.Grain);
        var capitals = new HashSet<int>(_polities.Standing().Select(p => p.Capital));
        int[] settlementPlusOne = _sim.State.IntField(SettlementSystem.SettlementField);
        double capitalGrain = 0.0, capitalPeople = 0.0, otherGrain = 0.0, otherPeople = 0.0;
        foreach (int cell in _land)
        {
            if (settlementPlusOne[cell] <= 0 || _polities.RulerOf(cell) < 0)
            {
                continue;
            }

            if (capitals.Contains(cell))
            {
                capitalGrain += stockpiles.Stock(cell, grain);
                capitalPeople += _people[cell];
            }
            else
            {
                otherGrain += stockpiles.Stock(cell, grain);
                otherPeople += _people[cell];
            }
        }

        r.CapitalGrainPerPerson = capitalPeople > 0.0 ? capitalGrain / capitalPeople : 0.0;
        r.OtherGrainPerPerson = otherPeople > 0.0 ? otherGrain / otherPeople : 0.0;
        for (int g = 0; g < _groups.IdCount; g++)
        {
            if (_groups.IsAlive(g) && _groups.Size(g) > 0.0 && _groups.Occupation(g) == _elite && !capitals.Contains(_groups.Cell(g)))
            {
                r.EliteOutsideCapitals++;
            }
        }
    }
}
