namespace Sim.Core.Engine;

/// <summary>
/// A calendar date derived from a tick count. One tick is one month.
/// Tick 0 is January of 10,000 BCE. There is no year zero: 1 BCE is followed by 1 CE.
/// </summary>
public readonly struct GameDate
{
    public const int MonthsPerYear = 12;
    public const int StartYearBce = 10000;

    private static readonly string[] MonthNames =
    {
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    };

    public GameDate(long tick)
    {
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), "Tick cannot be negative.");
        }

        Tick = tick;
    }

    public long Tick { get; }

    /// <summary>Month of the year, 1 to 12.</summary>
    public int Month => (int)(Tick % MonthsPerYear) + 1;

    /// <summary>Whole years since the start of the run.</summary>
    public long YearsElapsed => Tick / MonthsPerYear;

    public bool IsBce => YearsElapsed < StartYearBce;

    /// <summary>The year number within its era, always 1 or more.</summary>
    public long Year => IsBce ? StartYearBce - YearsElapsed : YearsElapsed - StartYearBce + 1;

    public string MonthName => MonthNames[Month - 1];

    /// <summary>The tick of January in the given year. Use a negative number for BCE: -10000 is 10,000 BCE.</summary>
    public static long TickOfYear(long year)
    {
        if (year == 0 || year < -StartYearBce)
        {
            throw new ArgumentOutOfRangeException(nameof(year), "There is no year zero, and runs start in 10,000 BCE.");
        }

        long yearsElapsed = year < 0 ? StartYearBce + year : StartYearBce + year - 1;
        return yearsElapsed * MonthsPerYear;
    }

    public override string ToString() => $"{MonthName} {Year} {(IsBce ? "BCE" : "CE")}";
}
