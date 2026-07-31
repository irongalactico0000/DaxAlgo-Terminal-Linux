using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TradingTerminal.SurfaceLab;

/// <summary>Small native Avalonia cross-section plot used by both Surface Lab slice viewers.</summary>
public sealed class SurfaceSlicePlot : Control
{
    private static readonly Typeface Mono =
        new("SFMono-Regular, Menlo, Cascadia Mono, Consolas, monospace");
    private static readonly IBrush BackgroundBrush = Brush(0xFF, 0x0B, 0x10, 0x16);
    private static readonly IBrush TextBrush = Brush(0xFF, 0x8B, 0x94, 0x9E);
    private static readonly IBrush TitleBrush = Brush(0xFF, 0xD0, 0xD7, 0xDE);
    private static readonly IBrush MarkerBrush = Brush(0xFF, 0xFF, 0x52, 0x52);
    private static readonly IPen BorderPen = Pen(0xFF, 0x30, 0x36, 0x3D, 1);
    private static readonly IPen GridPen = Pen(0x40, 0x6E, 0x76, 0x81, 0.6);
    private static readonly IPen LinePen = Pen(0xFF, 0x26, 0xC6, 0xDA, 1.6);
    private static readonly IPen MarkerPen = Pen(0xFF, 0xFF, 0x82, 0x82, 1);

    private double[] _axis = Array.Empty<double>();
    private double[] _values = Array.Empty<double>();
    private string _title = string.Empty;

    public SurfaceSlicePlot() => ClipToBounds = true;

    public void SetData(double[] axis, double[] values, string title)
    {
        _axis = axis;
        _values = values;
        _title = title;
        InvalidateVisual();
    }

    public void Clear()
    {
        _axis = Array.Empty<double>();
        _values = Array.Empty<double>();
        _title = string.Empty;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);
        context.DrawRectangle(null, BorderPen, bounds.Deflate(0.5));
        if (_axis.Length == 0 || _values.Length == 0 || Bounds.Width < 90 || Bounds.Height < 70)
            return;

        var valid = new List<(double X, double Y)>();
        for (var index = 0; index < _axis.Length && index < _values.Length; index++)
        {
            if (!double.IsNaN(_axis[index]) && !double.IsNaN(_values[index]))
                valid.Add((_axis[index], _values[index]));
        }
        if (valid.Count == 0)
        {
            DrawText(context, "No populated cells in this slice", 12, Bounds.Height / 2 - 7,
                TextBrush, 10.5);
            return;
        }

        var plot = new Rect(46, 26, Math.Max(1, Bounds.Width - 58), Math.Max(1, Bounds.Height - 48));
        var xMin = valid.Min(point => point.X);
        var xMax = valid.Max(point => point.X);
        var yMin = valid.Min(point => point.Y);
        var yMax = valid.Max(point => point.Y);
        ExpandRange(ref xMin, ref xMax);
        ExpandRange(ref yMin, ref yMax);

        Point Map((double X, double Y) point) => new(
            plot.Left + (point.X - xMin) / (xMax - xMin) * plot.Width,
            plot.Bottom - (point.Y - yMin) / (yMax - yMin) * plot.Height);

        const int gridLines = 4;
        for (var index = 0; index <= gridLines; index++)
        {
            var fraction = index / (double)gridLines;
            var y = plot.Top + fraction * plot.Height;
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var value = yMax - fraction * (yMax - yMin);
            DrawText(context, value.ToString("G4", CultureInfo.InvariantCulture),
                2, y - 6, TextBrush, 9.5);
        }

        var path = new PathGeometry();
        var figure = new PathFigure { StartPoint = Map(valid[0]) };
        path.Figures!.Add(figure);
        for (var index = 1; index < valid.Count; index++)
            figure.Segments!.Add(new LineSegment { Point = Map(valid[index]) });
        context.DrawGeometry(null, LinePen, path);

        var maximum = valid[0];
        for (var index = 1; index < valid.Count; index++)
            if (valid[index].Y > maximum.Y)
                maximum = valid[index];
        var maximumPoint = Map(maximum);
        context.DrawEllipse(MarkerBrush, MarkerPen, maximumPoint, 4.5, 4.5);

        DrawText(context, _title, 8, 6, TitleBrush, 10);
        DrawText(context, xMin.ToString("G4", CultureInfo.InvariantCulture),
            plot.Left, plot.Bottom + 4, TextBrush, 9.5);
        var highText = new FormattedText(
            xMax.ToString("G4", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Mono, 9.5, TextBrush);
        context.DrawText(highText, new Point(plot.Right - highText.Width, plot.Bottom + 4));
    }

    private static void ExpandRange(ref double min, ref double max)
    {
        if (max - min > 1e-12)
        {
            var padding = (max - min) * 0.04;
            min -= padding;
            max += padding;
            return;
        }
        var fallback = Math.Max(Math.Abs(min) * 0.05, 1e-6);
        min -= fallback;
        max += fallback;
    }

    private static void DrawText(
        DrawingContext context, string text, double x, double y, IBrush brush, double size)
    {
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush);
        context.DrawText(formatted, new Point(x, y));
    }

    private static IBrush Brush(byte alpha, byte red, byte green, byte blue) =>
        new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));

    private static IPen Pen(byte alpha, byte red, byte green, byte blue, double thickness) =>
        new Pen(Brush(alpha, red, green, blue), thickness);
}
