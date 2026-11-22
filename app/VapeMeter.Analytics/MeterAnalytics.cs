using System.Globalization;

namespace VapeMeter.Analytics;

public sealed record UsageInterval(DateOnly Start, DateOnly End, int Days);

public sealed record ForecastResult(
    DateOnly? Date,
    DateOnly? Earliest,
    DateOnly? Latest,
    int SampleCount,
    string Description);

public sealed record AnalyticsSnapshot(
    int Count,
    IReadOnlyList<UsageInterval> Intervals,
    double? AverageDays,
    int? MinDays,
    int? MaxDays,
    int? CurrentDays,
    ForecastResult Forecast,
    string Trend);

public static class MeterAnalytics
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static AnalyticsSnapshot Calculate(
        IEnumerable<DateOnly> replacementDates,
        DateOnly today,
        DateOnly? from = null,
        DateOnly? to = null)
    {
        ArgumentNullException.ThrowIfNull(replacementDates);
        if (from > to)
            throw new ArgumentException("The end date precedes the start date.", nameof(to));

        var dates = replacementDates.Where(date => date <= today).Distinct().Order().ToArray();
        var start = from ?? DateOnly.MinValue;
        var end = to ?? today;
        var count = dates.Count(date => date >= start && date <= end);
        var complete = GetIntervals(dates);
        var intervals = complete.Where(interval => interval.End >= start && interval.End <= end).ToArray();
        double? average = intervals.Length > 0 ? intervals.Average(interval => interval.Days) : null;
        var trend = from.HasValue && average.HasValue
            ? DescribeTrend(complete, start, end, average.Value)
            : string.Empty;

        if (dates.Length == 0)
        {
            return new AnalyticsSnapshot(
                count, intervals, average, null, null, null,
                new ForecastResult(null, null, null, 0, "Добавьте первую замену, чтобы начать наблюдение."),
                trend);
        }

        var last = dates[^1];
        var age = today.DayNumber - last.DayNumber;
        return new AnalyticsSnapshot(
            count,
            intervals,
            average,
            intervals.Length > 0 ? intervals.Min(interval => interval.Days) : null,
            intervals.Length > 0 ? intervals.Max(interval => interval.Days) : null,
            age,
            Forecast(complete, last, age),
            trend);
    }

    private static UsageInterval[] GetIntervals(DateOnly[] dates)
    {
        var intervals = new UsageInterval[Math.Max(0, dates.Length - 1)];
        for (var i = 0; i < intervals.Length; i++)
        {
            var start = dates[i];
            var end = dates[i + 1];
            intervals[i] = new UsageInterval(start, end, end.DayNumber - start.DayNumber);
        }
        return intervals;
    }

    private static string DescribeTrend(UsageInterval[] complete, DateOnly start, DateOnly end, double average)
    {
        var previousEnd = start.DayNumber - 1;
        var previousStart = previousEnd - (end.DayNumber - start.DayNumber);
        var previous = complete
            .Where(interval => interval.End.DayNumber >= previousStart && interval.End.DayNumber <= previousEnd)
            .Select(interval => interval.Days)
            .ToArray();
        if (previous.Length == 0)
            return string.Empty;

        var change = Math.Round(average - previous.Average(), 1, MidpointRounding.AwayFromZero);
        return change.ToString("+0.#;-0.#;0", Russian) + " д.";
    }

    private static ForecastResult Forecast(UsageInterval[] complete, DateOnly last, int age)
    {
        // The current cartridge has survived this long, so only comparable completed cycles inform its forecast.
        var sample = complete.Select(interval => interval.Days).Where(days => days >= age).Order().ToArray();
        if (sample.Length < 2)
        {
            var description = complete.Length < 2
                ? "Для прогноза нужны два завершённых цикла."
                : "Для текущего срока пока мало сопоставимых завершённых циклов.";
            return new ForecastResult(null, null, null, sample.Length, description);
        }

        var median = (int)Math.Round(Quantile(sample, 0.5), MidpointRounding.AwayFromZero);
        var predicted = ForecastDate(last, median);
        if (sample.Length == 2)
        {
            return new ForecastResult(
                predicted, null, null, sample.Length,
                "По двум сопоставимым срокам. Диапазон появится после третьего.");
        }

        var useQuartiles = sample.Length >= 8;
        var earliest = useQuartiles ? (int)Math.Floor(Quantile(sample, 0.25)) : sample[0];
        var latest = useQuartiles ? (int)Math.Ceiling(Quantile(sample, 0.75)) : sample[^1];
        var bounds = useQuartiles
            ? "Диапазон по центральной половине сроков."
            : "Диапазон от минимального до максимального срока.";

        return new ForecastResult(
            predicted, ForecastDate(last, earliest), ForecastDate(last, latest), sample.Length, bounds);
    }

    private static double Quantile(int[] ordered, double probability)
    {
        var index = (ordered.Length - 1) * probability;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower);
    }

    private static DateOnly ForecastDate(DateOnly last, int days) =>
        DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, last.DayNumber + days));
}
