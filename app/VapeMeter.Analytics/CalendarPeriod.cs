namespace VapeMeter.Analytics;

public readonly record struct CalendarPeriod(DateOnly Start, DateOnly End)
{
    public const int DaysPerPeriod = 30;

    public int MondayOffset => ((int)Start.DayOfWeek + 6) % 7;

    public static CalendarPeriod ForOffset(DateOnly today, int offset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var endDayNumber = today.DayNumber - (long)offset * DaysPerPeriod;
        if (endDayNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The period precedes the first supported date.");

        var start = DateOnly.FromDayNumber(Math.Max(0, (int)endDayNumber - DaysPerPeriod + 1));
        return new CalendarPeriod(start, DateOnly.FromDayNumber((int)endDayNumber));
    }

    public bool Contains(DateOnly date) => date >= Start && date <= End;

    public IEnumerable<DateOnly> Dates()
    {
        for (var dayNumber = Start.DayNumber; dayNumber <= End.DayNumber; dayNumber++)
            yield return DateOnly.FromDayNumber(dayNumber);
    }
}
