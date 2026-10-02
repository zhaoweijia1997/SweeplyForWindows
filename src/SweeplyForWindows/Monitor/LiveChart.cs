using System.Windows;
using System.Windows.Media;
using Sweeply.Core.Monitoring;

namespace SweeplyForWindows.Monitor;

/// <summary>
/// A small chart that scrolls with time: the newest value at the right edge, the oldest of the
/// window at the left. The first series is a line over a soft area, the second a dashed line in the
/// same colour. Points far apart in time (sampling was paused) are not joined.
/// </summary>
public sealed class LiveChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = Register<IReadOnlyList<ChartPoint>?>(nameof(Points), null);
    public static readonly DependencyProperty Points2Property = Register<IReadOnlyList<ChartPoint>?>(nameof(Points2), null);
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), 0.0);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 100.0);
    public static readonly DependencyProperty WindowSecondsProperty = Register(nameof(WindowSeconds), 120.0);
    public static readonly DependencyProperty MaxGapSecondsProperty = Register(nameof(MaxGapSeconds), 5.0);
    public static readonly DependencyProperty StrokeProperty = Register<Brush>(nameof(Stroke), Brushes.SteelBlue);
    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(LiveChart),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartPoint>? Points { get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<ChartPoint>? Points2 { get => (IReadOnlyList<ChartPoint>?)GetValue(Points2Property); set => SetValue(Points2Property, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }

    /// <summary>Points further apart than this are not joined: sampling stopped in between.</summary>
    public double MaxGapSeconds { get => (double)GetValue(MaxGapSecondsProperty); set => SetValue(MaxGapSecondsProperty, value); }

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Three faint lines at a quarter, half and three quarters of the height; none when null.</summary>
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    private Color? _fillColor;
    private Brush? _fill;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 2 || height < 2) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));

        if (GridBrush is Brush grid)
        {
            var gridPen = new Pen(grid, 1);
            gridPen.Freeze();
            for (int i = 1; i <= 3; i++)
            {
                double y = Math.Round(height * i / 4) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
            }
        }

        var line = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
        line.Freeze();
        var dashed = new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round, DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0) };
        dashed.Freeze();
        if (Points is { Count: > 1 } points) Draw(dc, points, width, height, line, AreaFill());
        if (Points2 is { Count: > 1 } points2) Draw(dc, points2, width, height, dashed, null);
        dc.Pop();
    }

    private void Draw(DrawingContext dc, IReadOnlyList<ChartPoint> points, double width, double height, Pen pen, Brush? fill)
    {
        double window = Math.Max(1, WindowSeconds), min = Minimum, range = Math.Max(1e-9, Maximum - Minimum);
        Point At(ChartPoint p) => new(
            width * (1 - p.SecondsAgo / window),
            height - 1 - Math.Clamp((p.Value - min) / range, 0, 1) * (height - 2));

        // One run per stretch without a gap.
        int start = 0;
        for (int i = 1; i <= points.Count; i++)
        {
            bool end = i == points.Count || points[i - 1].SecondsAgo - points[i].SecondsAgo > MaxGapSeconds;
            if (!end) continue;
            if (i - start > 1)
            {
                var run = new Point[i - start];
                for (int k = 0; k < run.Length; k++) run[k] = At(points[start + k]);
                if (fill is not null) dc.DrawGeometry(fill, null, Geometry(run, height, closed: true));
                dc.DrawGeometry(null, pen, Geometry(run, height, closed: false));
            }
            start = i;
        }
    }

    private static StreamGeometry Geometry(Point[] run, double height, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (closed)
            {
                // Down to the bottom at both ends, so the area under the line can be filled.
                ctx.BeginFigure(new Point(run[0].X, height), true, true);
                ctx.PolyLineTo(run, true, true);
                ctx.LineTo(new Point(run[^1].X, height), true, true);
            }
            else
            {
                ctx.BeginFigure(run[0], false, false);
                ctx.PolyLineTo(run[1..], true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The line's colour fading towards the bottom.</summary>
    private Brush? AreaFill()
    {
        if (Stroke is not SolidColorBrush solid) return null;
        if (_fill is not null && _fillColor == solid.Color) return _fill;
        var c = solid.Color;
        var brush = new LinearGradientBrush(Color.FromArgb(0x50, c.R, c.G, c.B), Color.FromArgb(0x06, c.R, c.G, c.B), 90);
        brush.Freeze();
        _fillColor = c;
        return _fill = brush;
    }
}
