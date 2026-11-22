using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using VapeMeter.Analytics;

namespace VapeMeter.UI;

internal sealed class UsagePlot : Control
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly IReadOnlyList<UsageInterval> _intervals;
    private readonly double? _average;
    private readonly Action<UsageInterval?> _show;
    private readonly int _ticks;
    private readonly double _maximum;
    private int _selected;
    private int? _hover;

    public UsagePlot(IReadOnlyList<UsageInterval> intervals, double? average, Action<UsageInterval?> show)
    {
        _intervals = intervals.OrderBy(interval => interval.End).ToArray();
        _average = average;
        _show = show;
        _selected = _intervals.Count - 1;
        var max = intervals.Max(interval => interval.Days);
        var group = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, max) / 4.0)));
        var ratio = max / 4.0 / group;
        var step = Math.Max(1, group * (ratio <= 1 ? 1 : ratio <= 2 ? 2 : ratio <= 5 ? 5 : 10));
        _ticks = (int)Math.Max(1, Math.Ceiling(max / step));
        _maximum = step * _ticks;
        SetActiveDescription();
    }

    private Rect PlotArea => new(31, 12, Math.Max(1, Bounds.Width - 49), Math.Max(1, Bounds.Height - 40));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Bounds.Width < 74 || Bounds.Height < 60)
        {
            return;
        }

        var area = PlotArea;
        var edge = DrawingBrush("VmLine", Colors.DarkSlateBlue);
        var muted = DrawingBrush("VmMuted", Colors.LightGray);
        var line = DrawingBrush("VmChartLine", Colors.MediumPurple);
        var focus = DrawingBrush("VmMint", Colors.MediumAquamarine);
        var fill = DrawingBrush("VmChartFill", Colors.MediumPurple) as ISolidColorBrush;
        var lineColor = (line as ISolidColorBrush)?.Color ?? Colors.MediumPurple;
        var stroke = new Pen(edge, 1);

        for (var tick = 0; tick <= _ticks; tick++)
        {
            var y = area.Bottom - area.Height * tick / _ticks;
            context.DrawLine(stroke, new Point(area.X, y), new Point(area.Right, y));
            var mark = (_maximum * tick / _ticks).ToString("0", Russian);
            var label = Label(mark, muted);
            context.DrawText(label, new Point(area.X - label.Width - 7, y - label.Height / 2));
        }

        var plotted = _intervals.Select((interval, i) => PointAt(area, i, interval.Days)).ToArray();
        var linePath = Spline(plotted, area, false);
        var areaPath = Spline(plotted, area, true);
        var baseColor = fill?.Color ?? Colors.MediumPurple;
        var wash = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(126, baseColor.R, baseColor.G, baseColor.B), 0),
                new GradientStop(Color.FromArgb(8, baseColor.R, baseColor.G, baseColor.B), 1),
            },
        };
        if (plotted.Length > 1)
        {
            context.DrawGeometry(wash, null, areaPath);
        }

        if (_average is double average)
        {
            var y = area.Bottom - Math.Clamp(average / _maximum, 0, 1) * area.Height;
            var dashed = new Pen(focus, 1.2, new DashStyle([5.0, 4.0], 0));
            context.DrawLine(dashed, new Point(area.X, y), new Point(area.Right, y));
        }
        if (plotted.Length > 1)
        {
            context.DrawGeometry(null, new Pen(WithOpacity(lineColor, 38), 8), linePath);
            context.DrawGeometry(null, new Pen(line, 2.5), linePath);
        }

        var active = _hover ?? _selected;
        if ((uint)active < (uint)plotted.Length)
        {
            var point = plotted[active];
            context.DrawLine(new Pen(WithOpacity(lineColor, 94), 1), point, new Point(point.X, area.Bottom));
            context.DrawEllipse(WithOpacity(lineColor, 56), null, point, 9, 9);
        }
        var dotBorder = new Pen(DrawingBrush("VmCardEdge", Colors.Plum), 1);
        for (var i = 0; i < plotted.Length; i++)
        {
            var radius = i == active ? 4.4 : plotted.Length > 22 ? 2 : 3.2;
            context.DrawEllipse(i == active ? focus : line, dotBorder, plotted[i], radius, radius);
        }

        var labelCount = Math.Max(2, Math.Min(6, (int)(area.Width / 72)));
        var spacing = Math.Max(1, (int)Math.Ceiling((double)_intervals.Count / labelCount));
        var previousRight = 0.0;
        for (var i = 0; i < _intervals.Count; i++)
        {
            if (i % spacing != 0 && i + 1 != _intervals.Count)
            {
                continue;
            }
            var label = Label(_intervals[i].End.ToString("d MMM", Russian), muted);
            var x = Math.Clamp(plotted[i].X - label.Width / 2, 0, Math.Max(0, Bounds.Width - label.Width));
            if (x < previousRight)
            {
                continue;
            }
            context.DrawText(label, new Point(x, area.Bottom + 8));
            previousRight = x + label.Width + 8;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var area = PlotArea;
        var hovered = area.Contains(point) ? NearestIndex(point.X, area) : (int?)null;
        if (_hover != hovered)
        {
            _hover = hovered;
            SetActiveDescription();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        SetActiveDescription();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        if (PlotArea.Contains(point))
        {
            _selected = NearestIndex(point.X, PlotArea);
            Focus();
            SetActiveDescription();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End))
        {
            return;
        }
        _hover = null;
        _selected = e.Key switch
        {
            Key.Home => 0,
            Key.End => _intervals.Count - 1,
            Key.Left => Math.Max(0, _selected - 1),
            _ => Math.Min(_intervals.Count - 1, _selected + 1),
        };
        SetActiveDescription();
        e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateVisual();
        }
    }

    private Point PointAt(Rect area, int index, int days)
    {
        var x = _intervals.Count == 1 ? area.Center.X : area.X + area.Width * index / (_intervals.Count - 1);
        return new Point(x, area.Bottom - area.Height * days / _maximum);
    }

    private int NearestIndex(double x, Rect area) =>
        Math.Clamp((int)Math.Round((x - area.X) / area.Width * (_intervals.Count - 1)), 0, _intervals.Count - 1);

    private void SetActiveDescription()
    {
        var interval = _intervals[_hover ?? _selected];
        _show(interval);
        AutomationProperties.SetName(this, $"График срока службы. {interval.End.ToString("d MMMM", Russian)}. Дней: {interval.Days}");
        InvalidateVisual();
    }

    private static StreamGeometry Spline(IReadOnlyList<Point> points, Rect area, bool close)
    {
        var curve = new StreamGeometry();
        using var figure = curve.Open();
        figure.BeginFigure(points[0], close);
        for (var i = 1; i < points.Count; i++)
        {
            var before = points[i - 1];
            var after = points[i];
            var middle = (before.X + after.X) / 2;
            figure.CubicBezierTo(new Point(middle, before.Y), new Point(middle, after.Y), after);
        }
        if (close)
        {
            figure.LineTo(new Point(points[^1].X, area.Bottom));
            figure.LineTo(new Point(points[0].X, area.Bottom));
        }
        figure.EndFigure(close);
        return curve;
    }

    private IBrush DrawingBrush(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush : new SolidColorBrush(fallback);

    private static FormattedText Label(string value, IBrush brush) =>
        new(value, Russian, FlowDirection.LeftToRight, Typeface.Default, 11, brush);

    private static IBrush WithOpacity(Color color, byte alpha) =>
        new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
}

internal sealed class ForecastOrbit(double proportion) : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width * .39, Bounds.Height * .39);
        if (radius < 6)
        {
            return;
        }
        var track = Resolve("VmOrbitTrack", Colors.DarkSlateGray);
        var mint = Resolve("VmMint", Colors.MediumAquamarine);
        var color = (mint as ISolidColorBrush)?.Color ?? Colors.MediumAquamarine;
        var radial = new RadialGradientBrush
        {
            Center = new RelativePoint(.5, .5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(42, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        context.DrawEllipse(radial, null, center, radius * 1.3, radius * 1.3);
        var faint = new SolidColorBrush(Color.FromArgb(55, color.R, color.G, color.B));
        for (var i = -2; i <= 2; i++)
        {
            var x = center.X + i * 22;
            var y = center.Y + i * 22;
            context.DrawLine(new Pen(faint, .65), new Point(x, center.Y - radius - 7), new Point(x, center.Y + radius + 7));
            context.DrawLine(new Pen(faint, .65), new Point(center.X - radius - 17, y), new Point(center.X + radius + 17, y));
        }
        context.DrawEllipse(null, new Pen(track, 2), center, radius, radius);
        context.DrawEllipse(null, new Pen(faint, 1, new DashStyle([2.0, 3.5], 0)), center, radius + 8, radius + 8);
        context.DrawEllipse(null, new Pen(faint), center, radius - 7, radius - 7);
        if (proportion <= 0)
        {
            return;
        }
        var angle = -Math.PI / 2 + Math.Min(.995, proportion) * 2 * Math.PI;
        var tip = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
        var arc = new StreamGeometry();
        using (var figure = arc.Open())
        {
            figure.BeginFigure(new Point(center.X, center.Y - radius), false);
            figure.ArcTo(tip, new Size(radius, radius), 0, proportion > .5, SweepDirection.Clockwise);
            figure.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(faint, 9), arc);
        context.DrawGeometry(null, new Pen(mint, 3), arc);
        context.DrawEllipse(mint, null, tip, 3.5, 3.5);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateVisual();
        }
    }

    private IBrush Resolve(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush : new SolidColorBrush(fallback);
}
