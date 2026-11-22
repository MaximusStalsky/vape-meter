using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VapeMeter.Analytics;
using static VapeMeter.UI.VapeVisuals;

namespace VapeMeter.UI;

public sealed partial class MainView
{
    private Control BuildOverview()
    {
        var stats = CalculatePeriod();
        var calendar = BuildCalendar();
        var forecast = BuildForecast(stats);
        Control firstRow;
        if (IsCompact)
        {
            firstRow = Column(12, calendar, forecast);
        }
        else
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("1.65*,*"), ColumnSpacing = 12 };
            Grid.SetColumn(forecast, 1);
            grid.Children.Add(calendar);
            grid.Children.Add(forecast);
            firstRow = grid;
        }
        return Column(IsCondensedOverview ? 9 : 12, firstRow, BuildMetrics(stats), BuildChart(stats));
    }

    private Control BuildAnalytics()
    {
        var stats = CalculatePeriod();
        var period = Inset(SideBySide(Heading("calendar", "Период статистики", DateRange(Period)), PeriodButtons()));
        return Column(12, period, BuildMetrics(stats), BuildChart(stats), BuildForecast(stats));
    }

    private AnalyticsSnapshot CalculatePeriod() =>
        MeterAnalytics.Calculate(_records.Select(record => record.Date), Today, Period.Start, Period.End);

    private Control BuildMetrics(AnalyticsSnapshot stats)
    {
        var average = stats.AverageDays is double averageDays ? $"{averageDays.ToString("0.#", Russian)} дн." : "—";
        var trend = string.IsNullOrWhiteSpace(stats.Trend) ? "Завершённые циклы" : $"К предыдущему периоду {stats.Trend}";
        var cards = new[]
        {
            Metric("clock", "Средний срок", average, trend),
            Metric("layers", "Замены", stats.Count.ToString(Russian), "За выбранный период"),
            Metric("down", "Мин. срок", stats.MinDays is int min ? $"{min} дн." : "—", ""),
            Metric("up", "Макс. срок", stats.MaxDays is int max ? $"{max} дн." : "—", ""),
        };
        var columns = _layout == LayoutMode.Wide ? 4 : 2;
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        for (var i = 0; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        for (var i = 0; i < cards.Length; i++)
        {
            var row = i / columns;
            if (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            Grid.SetRow(cards[i], row);
            Grid.SetColumn(cards[i], i % columns);
            grid.Children.Add(cards[i]);
        }
        return grid;
    }

    private Border Metric(string icon, string title, string value, string subtitle)
    {
        var figure = Text(value, IsCondensedOverview ? 22 : 24, FontWeight.Bold);
        var labels = Column(1, Text(title, 12, tone: "muted", wrap: true), figure);
        if (subtitle.Length > 0)
        {
            labels.Children.Add(Text(subtitle, 11, tone: "muted", wrap: true));
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        var glyph = IconTile(icon, icon == "up", 32);
        glyph.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(labels, 1);
        row.Children.Add(glyph);
        row.Children.Add(labels);
        var card = Card(row);
        card.Padding = new Thickness(10, IsCondensedOverview ? 7 : 10);
        return card;
    }

    private Control BuildForecast(AnalyticsSnapshot stats)
    {
        var forecast = stats.Forecast;
        var remaining = forecast.Date is DateOnly date ? Math.Max(0, date.DayNumber - Today.DayNumber) : (int?)null;
        var current = stats.CurrentDays;
        var dateLabel = forecast.Date is DateOnly next ? ShortDate(next) : "—";
        var timing = remaining switch
        {
            0 => "Сегодня",
            int number => $"Через {Duration(number)}",
            _ => "Мало данных",
        };
        var range = forecast.Earliest is DateOnly early && forecast.Latest is DateOnly late
            ? early == late ? ShortDate(early) : $"{ShortDate(early)} — {ShortDate(late)}"
            : "—";
        var proportion = remaining is int ahead && current is int age && ahead + age > 0
            ? Math.Clamp((double)age / (ahead + age), .05, 1)
            : 0;

        var hero = new Grid { Height = IsCondensedOverview ? 126 : 161 };
        hero.Children.Add(new ForecastOrbit(proportion));
        var dateHint = Text(dateLabel, 27, FontWeight.Bold, "mint");
        dateHint.HorizontalAlignment = HorizontalAlignment.Center;
        var when = Text(timing, 11, tone: "muted");
        when.HorizontalAlignment = HorizontalAlignment.Center;
        var center = Column(1, dateHint, when);
        center.VerticalAlignment = VerticalAlignment.Center;
        center.HorizontalAlignment = HorizontalAlignment.Center;
        hero.Children.Add(center);
        AutomationProperties.SetAutomationId(dateHint, "VapeMeter.ForecastDate");

        var rangePanel = ValuePanel("Диапазон дат", range);
        var currentPanel = ValuePanel("Текущий картридж", current is int days ? Duration(days) : "—");
        var details = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6 };
        Grid.SetColumn(currentPanel, 1);
        details.Children.Add(rangePanel);
        details.Children.Add(currentPanel);

        return OverviewCard(Column(IsCondensedOverview ? 7 : 9,
            Heading("spark", "Следующая замена", "Прогноз по вашей истории", mint: true),
            hero, details,
            Text(forecast.Description, 12, tone: "muted", wrap: true)), "VapeMeter.Forecast");
    }

    private static Control ValuePanel(string label, string value) =>
        Inset(Column(4, Text(label, 11, tone: "muted", wrap: true), Text(value, 14, FontWeight.SemiBold, wrap: true)));

    private Control BuildChart(AnalyticsSnapshot snapshot)
    {
        var stats = _chartAll ? MeterAnalytics.Calculate(_records.Select(record => record.Date), Today) : snapshot;
        var heading = Heading("chart", "Срок службы картриджей", "Дни между заменами");
        var period = ActionButton("Период", () => SetChartAll(false), _chartAll ? "" : "active", id: "VapeMeter.ChartPeriod");
        var all = ActionButton("Вся история", () => SetChartAll(true), _chartAll ? "active" : "", id: "VapeMeter.ChartAll");
        var chart = Column(IsCondensedOverview ? 6 : 9, SideBySide(heading, Wrap(period, all)));

        if (stats.Intervals.Count == 0)
        {
            chart.Children.Add(Inset(Text("Для графика отметьте хотя бы две замены в выбранном диапазоне.", 13, tone: "muted", wrap: true)));
            return OverviewCard(chart, "VapeMeter.UsageChart");
        }

        var detail = Text("", 13, FontWeight.SemiBold, wrap: true);
        void ShowUsage(UsageInterval? interval)
        {
            detail.Text = interval is null ? "Наведите на точку или выберите её стрелками"
                : $"{ShortDate(interval.Start)} — {DateText(interval.End)}   ·   {Duration(interval.Days)}";
        }

        var plot = new UsagePlot(stats.Intervals, stats.AverageDays, ShowUsage)
        {
            Height = IsCondensedOverview ? (Period.MondayOffset + 30 > 35 ? 106 : 132) : IsCompact ? 158 : 184,
            Focusable = true,
        };
        AutomationProperties.SetAutomationId(plot, "VapeMeter.ChartPlot");
        ShowUsage(stats.Intervals[^1]);
        chart.Children.Add(plot);

        var average = stats.AverageDays is double averageDays ? $"Среднее {averageDays.ToString("0.#", Russian)} дн." : "";
        var line = Dot(violet: true);
        line.VerticalAlignment = VerticalAlignment.Center;
        var footer = Wrap(Row(6, line, Text("Завершённый цикл", 11, tone: "muted")),
            Text(average, 11, tone: "mint"));
        if (IsCondensedOverview)
        {
            var caption = Inset(SideBySide(detail, footer));
            caption.Padding = new Thickness(9, 4);
            chart.Children.Add(caption);
        }
        else
        {
            chart.Children.Add(footer);
            chart.Children.Add(Inset(detail));
        }
        return OverviewCard(chart, "VapeMeter.UsageChart");
    }

    private Border OverviewCard(Control content, string id)
    {
        var card = Card(content, id);
        if (IsCondensedOverview)
        {
            card.Padding = new Thickness(12);
        }
        return card;
    }

    private void SetChartAll(bool all)
    {
        _chartAll = all;
        Refresh();
    }

    private static string Duration(int days)
    {
        var word = (days % 100) is >= 11 and <= 14 ? "дней" : (days % 10) switch
        {
            1 => "день",
            2 or 3 or 4 => "дня",
            _ => "дней",
        };
        return $"{days} {word}";
    }
}
