using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TradingTerminal.Core.Quant.Surfaces;

namespace TradingTerminal.SurfaceLab;

/// <summary>
/// Seek-free native Avalonia 3D surface renderer. It keeps the numerical surface in the copied
/// view model/Core implementation and replaces only HelixToolkit's WPF viewport: finite cells are
/// depth-sorted and projected, NaN cells remain holes, slice moves do not rebuild the mesh, and
/// pointer gestures provide orbit, zoom, and pan.
/// </summary>
public sealed class SurfacePlot3D : Control
{
    private const double SurfaceHeight = 0.55;
    private const int PaletteSteps = 128;

    private static readonly Typeface UiTypeface =
        new("SF Pro Text, Segoe UI, Arial, sans-serif");
    private static readonly IBrush BackgroundBrush = Solid(0xFF, 0x09, 0x0D, 0x12);
    private static readonly IBrush MutedTextBrush = Solid(0xFF, 0x8B, 0x94, 0x9E);
    private static readonly IBrush PrimaryTextBrush = Solid(0xFF, 0xE6, 0xED, 0xF3);
    private static readonly IBrush PeakBrush = Solid(0xFF, 0xFF, 0x52, 0x52);
    private static readonly IBrush PeakLabelBrush = Solid(0xE8, 0x30, 0x10, 0x10);
    private static readonly IBrush SliceXBrush = Solid(0x30, 0xE0, 0x50, 0x50);
    private static readonly IBrush SliceYBrush = Solid(0x30, 0x50, 0xC0, 0x50);

    private static readonly IPen MeshPen = Pen(0x35, 0x08, 0x0A, 0x0D, 0.55);
    private static readonly IPen BoundsPen = Pen(0x66, 0x6E, 0x76, 0x81, 0.8);
    private static readonly IPen XAxisPen = Pen(0xFF, 0xF0, 0x72, 0x72, 1.8);
    private static readonly IPen YAxisPen = Pen(0xFF, 0x3F, 0xB9, 0x50, 1.8);
    private static readonly IPen ZAxisPen = Pen(0xFF, 0x58, 0xA6, 0xFF, 1.8);
    private static readonly IPen PeakPen = Pen(0xFF, 0xFF, 0x52, 0x52, 2.2);
    private static readonly IPen SliceXPen = Pen(0xB0, 0xE0, 0x50, 0x50, 1.0);
    private static readonly IPen SliceYPen = Pen(0xB0, 0x50, 0xC0, 0x50, 1.0);

    private static readonly IBrush[] ValuePalette = BuildPalette(new[]
    {
        new ColorStop(0.00, 0x0D, 0x2B, 0x6B),
        new ColorStop(0.25, 0x12, 0x74, 0xB8),
        new ColorStop(0.50, 0x19, 0xB5, 0x8A),
        new ColorStop(0.75, 0xE8, 0xC5, 0x2A),
        new ColorStop(1.00, 0xE6, 0x3C, 0x12),
    });
    private static readonly IBrush[] RobustnessPalette = BuildPalette(new[]
    {
        new ColorStop(0.00, 0x14, 0xA0, 0x44),
        new ColorStop(0.45, 0x9A, 0xC1, 0x2C),
        new ColorStop(0.70, 0xE8, 0xA8, 0x1E),
        new ColorStop(1.00, 0xD8, 0x22, 0x18),
    });

    private readonly List<SurfaceCell> _cells = new();
    private readonly List<ProjectedCell> _projectedCells = new();
    private SurfaceLabViewModel? _viewModel;
    private SurfaceGridResult? _result;
    private Vertex3? _peak;
    private string _peakText = string.Empty;
    private string _zLowLabel = string.Empty;
    private string _zHighLabel = string.Empty;
    private double _zTop = SurfaceHeight;
    private bool _useRobustnessPalette;
    private bool _projectionDirty = true;
    private Size _projectionSize;

    private double _yaw = -0.72;
    private double _pitch = 0.58;
    private double _zoom = 0.86;
    private double _panX;
    private double _panY;
    private bool _dragging;
    private bool _panning;
    private Point _lastPointer;

    public SurfacePlot3D()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public SurfaceLabViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value))
                return;
            if (_viewModel is not null)
            {
                _viewModel.SurfaceUpdated -= OnSurfaceUpdated;
                _viewModel.SliceChanged -= OnSliceChanged;
            }
            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.SurfaceUpdated += OnSurfaceUpdated;
                _viewModel.SliceChanged += OnSliceChanged;
            }
            RebuildMesh();
        }
    }

    private void OnSurfaceUpdated(object? sender, EventArgs e) => RebuildMesh();

    private void OnSliceChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void RebuildMesh()
    {
        _cells.Clear();
        _projectedCells.Clear();
        _projectionDirty = true;
        _result = _viewModel?.Result;
        _peak = null;
        _peakText = string.Empty;
        if (_result is not { Rows: >= 2, Columns: >= 2 } result || _viewModel is null)
        {
            InvalidateVisual();
            return;
        }

        var (zMin, zMax) = Range(result.Z);
        if (double.IsNaN(zMin))
        {
            InvalidateVisual();
            return;
        }

        _zTop = SurfaceHeight * _viewModel.HeightScale;
        var zSpan = Math.Max(zMax - zMin, 1e-12);
        var colorGrid = _viewModel.RobustnessColorMode ? result.Robustness : result.W;
        var (colorMin, colorMax) = _viewModel.RobustnessColorMode ? (0d, 1d) : Range(colorGrid);
        var colorSpan = Math.Max(colorMax - colorMin, 1e-12);
        _useRobustnessPalette = _viewModel.RobustnessColorMode;
        _zLowLabel = SurfaceAxisFormats.Format(zMin, result.ZFormat);
        _zHighLabel = SurfaceAxisFormats.Format(zMax, result.ZFormat);

        Vertex3 Vertex(int row, int column)
        {
            var z = result.Z[row, column];
            var color = colorGrid[row, column];
            var normalizedColor = double.IsNaN(color) || double.IsNaN(colorMin)
                ? 0d
                : Math.Clamp((color - colorMin) / colorSpan, 0d, 1d);
            return new Vertex3(
                column / (double)(result.Columns - 1),
                row / (double)(result.Rows - 1),
                (z - zMin) / zSpan * _zTop,
                normalizedColor);
        }

        for (var row = 0; row < result.Rows - 1; row++)
        {
            for (var column = 0; column < result.Columns - 1; column++)
            {
                if (double.IsNaN(result.Z[row, column]) ||
                    double.IsNaN(result.Z[row, column + 1]) ||
                    double.IsNaN(result.Z[row + 1, column]) ||
                    double.IsNaN(result.Z[row + 1, column + 1]))
                    continue;
                _cells.Add(new SurfaceCell(
                    Vertex(row, column),
                    Vertex(row, column + 1),
                    Vertex(row + 1, column + 1),
                    Vertex(row + 1, column)));
            }
        }

        if (_viewModel.ShowPeakMarker && SurfaceGridAnalysis.FindMax(result.Z) is { IsValid: true } peak)
        {
            _peak = new Vertex3(
                peak.Col / (double)(result.Columns - 1),
                peak.Row / (double)(result.Rows - 1),
                (peak.Value - zMin) / zSpan * _zTop,
                1d);
            _peakText = $"{result.XName} = {result.XLabels[peak.Col]}  |  " +
                        $"{result.YName} = {result.YLabels[peak.Row]}\n" +
                        $"{result.ZName} = {SurfaceAxisFormats.Format(peak.Value, result.ZFormat)}";
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        if (_result is not { } result || _cells.Count == 0 || Bounds.Width < 120 || Bounds.Height < 120)
            return;

        var plot = new Rect(54, 28, Math.Max(1, Bounds.Width - 92), Math.Max(1, Bounds.Height - 78));
        DrawBounds(context, plot);
        EnsureProjection(plot);

        var palette = _useRobustnessPalette ? RobustnessPalette : ValuePalette;
        foreach (var cell in _projectedCells)
        {
            var index = Math.Clamp((int)Math.Round(cell.Color * (PaletteSteps - 1)), 0, PaletteSteps - 1);
            context.DrawGeometry(palette[index], MeshPen, cell.Geometry);
        }

        DrawSlicePlanes(context, plot);
        DrawAxes(context, plot, result);
        DrawPeak(context, plot);
    }

    /// <summary>Projection is cached across slice moves. Only a new surface, camera gesture, or
    /// viewport resize rebuilds these geometries; moving a slice redraws the two planes only.</summary>
    private void EnsureProjection(Rect plot)
    {
        if (!_projectionDirty && _projectionSize.Equals(plot.Size))
            return;
        _projectedCells.Clear();
        foreach (var cell in _cells)
        {
            var a = Project(cell.A, plot, out var da);
            var b = Project(cell.B, plot, out var db);
            var c = Project(cell.C, plot, out var dc);
            var d = Project(cell.D, plot, out var dd);
            _projectedCells.Add(new ProjectedCell(
                Polygon(a, b, c, d),
                (da + db + dc + dd) * 0.25,
                (cell.A.Color + cell.B.Color + cell.C.Color + cell.D.Color) * 0.25));
        }
        _projectedCells.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));
        _projectionSize = plot.Size;
        _projectionDirty = false;
    }

    private void DrawBounds(DrawingContext context, Rect plot)
    {
        Vertex3[] corners =
        {
            new(0, 0, 0, 0), new(1, 0, 0, 0), new(1, 1, 0, 0), new(0, 1, 0, 0),
            new(0, 0, _zTop, 0), new(1, 0, _zTop, 0), new(1, 1, _zTop, 0), new(0, 1, _zTop, 0),
        };
        (int A, int B)[] edges =
        {
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
        };
        foreach (var edge in edges)
            context.DrawLine(BoundsPen,
                Project(corners[edge.A], plot, out _),
                Project(corners[edge.B], plot, out _));
    }

    private void DrawSlicePlanes(DrawingContext context, Rect plot)
    {
        if (_viewModel is null || _result is not { Rows: >= 2, Columns: >= 2 } result)
            return;
        var x = Math.Clamp(_viewModel.SliceXIndex, 0, result.Columns - 1) / (double)(result.Columns - 1);
        var y = Math.Clamp(_viewModel.SliceYIndex, 0, result.Rows - 1) / (double)(result.Rows - 1);
        var x0 = Project(new Vertex3(x, 0, 0, 0), plot, out _);
        var x1 = Project(new Vertex3(x, 1, 0, 0), plot, out _);
        var x2 = Project(new Vertex3(x, 1, _zTop, 0), plot, out _);
        var x3 = Project(new Vertex3(x, 0, _zTop, 0), plot, out _);
        context.DrawGeometry(SliceXBrush, SliceXPen, Polygon(x0, x1, x2, x3));

        var y0 = Project(new Vertex3(0, y, 0, 0), plot, out _);
        var y1 = Project(new Vertex3(1, y, 0, 0), plot, out _);
        var y2 = Project(new Vertex3(1, y, _zTop, 0), plot, out _);
        var y3 = Project(new Vertex3(0, y, _zTop, 0), plot, out _);
        context.DrawGeometry(SliceYBrush, SliceYPen, Polygon(y0, y1, y2, y3));
    }

    private void DrawAxes(DrawingContext context, Rect plot, SurfaceGridResult result)
    {
        var origin = Project(new Vertex3(0, 0, 0, 0), plot, out _);
        var xEnd = Project(new Vertex3(1, 0, 0, 0), plot, out _);
        var yEnd = Project(new Vertex3(0, 1, 0, 0), plot, out _);
        var zEnd = Project(new Vertex3(0, 0, _zTop, 0), plot, out _);
        context.DrawLine(XAxisPen, origin, xEnd);
        context.DrawLine(YAxisPen, origin, yEnd);
        context.DrawLine(ZAxisPen, origin, zEnd);

        DrawText(context, result.XLabels[0], origin + new Vector(-7, 7), MutedTextBrush, 10.5);
        DrawText(context, $"{result.XName}  {result.XLabels[^1]}", xEnd + new Vector(7, -7),
            Solid(0xFF, 0xF0, 0x72, 0x72), 10.5);
        DrawText(context, $"{result.YName}  {result.YLabels[^1]}", yEnd + new Vector(7, 3),
            Solid(0xFF, 0x3F, 0xB9, 0x50), 10.5);
        DrawText(context, _zLowLabel, origin + new Vector(-42, -7), MutedTextBrush, 10.5);
        DrawText(context, $"{result.ZName}  {_zHighLabel}", zEnd + new Vector(7, -12),
            Solid(0xFF, 0x58, 0xA6, 0xFF), 10.5);
    }

    private void DrawPeak(DrawingContext context, Rect plot)
    {
        if (_peak is not { } peak)
            return;
        var peakPoint = Project(peak, plot, out _);
        var pinTop = Project(peak with { Z = peak.Z + 0.14 }, plot, out _);
        context.DrawLine(PeakPen, peakPoint, pinTop);
        context.DrawEllipse(PeakBrush, PeakPen, pinTop, 5, 5);

        var text = new FormattedText(
            _peakText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            UiTypeface, 10.5, PrimaryTextBrush);
        var labelOrigin = pinTop + new Vector(9, -text.Height - 6);
        var labelRect = new Rect(labelOrigin - new Vector(5, 4),
            new Size(text.Width + 10, text.Height + 8));
        context.DrawRectangle(PeakLabelBrush, PeakPen, labelRect, 3, 3);
        context.DrawText(text, labelOrigin);
    }

    private Point Project(Vertex3 vertex, Rect plot, out double depth)
    {
        var x = vertex.X - 0.5;
        var y = vertex.Y - 0.5;
        var z = vertex.Z - _zTop * 0.34;

        var cosYaw = Math.Cos(_yaw);
        var sinYaw = Math.Sin(_yaw);
        var yawX = x * cosYaw - y * sinYaw;
        var yawY = x * sinYaw + y * cosYaw;

        var cosPitch = Math.Cos(_pitch);
        var sinPitch = Math.Sin(_pitch);
        var vertical = z * cosPitch - yawY * sinPitch;
        depth = z * sinPitch + yawY * cosPitch;
        var perspective = 3d / Math.Max(1.4, 3d - depth);
        var scale = Math.Min(plot.Width, plot.Height) * 0.72 * _zoom;
        return new Point(
            plot.Center.X + _panX + yawX * perspective * scale,
            plot.Center.Y + _panY - vertical * perspective * scale);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        if (e.ClickCount == 2)
        {
            ResetCamera();
            e.Handled = true;
            return;
        }
        _dragging = true;
        _panning = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _lastPointer = point.Position;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;
        var position = e.GetPosition(this);
        var delta = position - _lastPointer;
        _lastPointer = position;
        if (_panning)
        {
            _panX += delta.X;
            _panY += delta.Y;
        }
        else
        {
            _yaw += delta.X * 0.010;
            _pitch = Math.Clamp(_pitch - delta.Y * 0.010, -0.12, 1.38);
        }
        _projectionDirty = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.12, e.Delta.Y), 0.35, 2.8);
        _projectionDirty = true;
        InvalidateVisual();
        e.Handled = true;
    }

    private void ResetCamera()
    {
        _yaw = -0.72;
        _pitch = 0.58;
        _zoom = 0.86;
        _panX = 0;
        _panY = 0;
        _projectionDirty = true;
        InvalidateVisual();
    }

    private static StreamGeometry Polygon(Point a, Point b, Point c, Point d)
    {
        var geometry = new StreamGeometry();
        using var drawing = geometry.Open();
        drawing.BeginFigure(a, true);
        drawing.LineTo(b);
        drawing.LineTo(c);
        drawing.LineTo(d);
        drawing.EndFigure(true);
        return geometry;
    }

    private static (double Min, double Max) Range(double[,] grid)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var value in grid)
        {
            if (double.IsNaN(value))
                continue;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
        return min <= max ? (min, max) : (double.NaN, double.NaN);
    }

    private static IBrush[] BuildPalette(IReadOnlyList<ColorStop> stops)
    {
        var palette = new IBrush[PaletteSteps];
        for (var index = 0; index < palette.Length; index++)
        {
            var position = index / (double)(palette.Length - 1);
            var stopIndex = 0;
            while (stopIndex < stops.Count - 2 && position > stops[stopIndex + 1].Position)
                stopIndex++;
            var start = stops[stopIndex];
            var end = stops[stopIndex + 1];
            var fraction = end.Position > start.Position
                ? (position - start.Position) / (end.Position - start.Position)
                : 0d;
            byte Lerp(byte from, byte to) => (byte)Math.Round(from + (to - from) * fraction);
            palette[index] = Solid(0xFF,
                Lerp(start.Red, end.Red), Lerp(start.Green, end.Green), Lerp(start.Blue, end.Blue));
        }
        return palette;
    }

    private static void DrawText(
        DrawingContext context, string text, Point origin, IBrush brush, double size)
    {
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            UiTypeface, size, brush);
        context.DrawText(formatted, origin);
    }

    private static IBrush Solid(byte alpha, byte red, byte green, byte blue) =>
        new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));

    private static IPen Pen(byte alpha, byte red, byte green, byte blue, double thickness) =>
        new Pen(Solid(alpha, red, green, blue), thickness);

    private readonly record struct Vertex3(double X, double Y, double Z, double Color);
    private readonly record struct SurfaceCell(Vertex3 A, Vertex3 B, Vertex3 C, Vertex3 D);
    private readonly record struct ProjectedCell(StreamGeometry Geometry, double Depth, double Color);
    private readonly record struct ColorStop(double Position, byte Red, byte Green, byte Blue);
}
