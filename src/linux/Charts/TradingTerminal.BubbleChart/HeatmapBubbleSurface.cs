using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.BubbleChart;

/// <summary>
/// Native Avalonia renderer for the Professional bubble heatmap. It preserves the Windows surface's
/// liquidity palette, time-uniform columns, mid-price track, volume-scaled trade bubbles, large-lot
/// rings, axes, and redraw-on-demand contract.
/// </summary>
public sealed class HeatmapBubbleSurface : Control
{
    private const double LeftAxisWidth = 60;
    private const double TopPadding = 8;
    private const double BottomAxisHeight = 24;
    private const double RightPadding = 10;
    private const int HeatSteps = 64;

    private static readonly Typeface Mono =
        new("Cascadia Mono, SFMono-Regular, Menlo, Consolas, monospace");

    private static readonly IBrush BackgroundBrush = Brush(0xFF, 0x0A, 0x0A, 0x0F);
    private static readonly IBrush AxisTextBrush = Brush(0xFF, 0x9E, 0x9E, 0x9E);
    private static readonly IBrush PriceTextBrush = Brush(0xFF, 0xC8, 0xC8, 0xC8);
    private static readonly IBrush BuyBubbleBrush = Brush(0xE6, 0x3D, 0xD9, 0x8A);
    private static readonly IBrush SellBubbleBrush = Brush(0xE6, 0xFF, 0x5A, 0x57);
    private static readonly IBrush NeutralBubbleBrush = Brush(0xC0, 0xCF, 0xCF, 0xCF);

    private static readonly IPen GridPen = Pen(0x22, 0x88, 0x88, 0x88, 0.5);
    private static readonly IPen MidPen = Pen(0xCC, 0xEC, 0xEC, 0xEC, 1.1);
    private static readonly IPen BubbleOutlinePen = Pen(0x55, 0xFF, 0xFF, 0xFF, 0.6);
    private static readonly IPen LargeBubblePen = Pen(0xF0, 0xFF, 0xFF, 0xFF, 1.4);
    private static readonly IBrush[] HeatPalette = BuildHeatPalette();

    private BubbleChartViewModel? _viewModel;

    public HeatmapBubbleSurface() => ClipToBounds = true;

    public BubbleChartViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value))
                return;

            if (_viewModel is not null)
                _viewModel.SurfaceChanged -= OnSurfaceChanged;
            _viewModel = value;
            if (_viewModel is not null)
                _viewModel.SurfaceChanged += OnSurfaceChanged;
            InvalidateVisual();
        }
    }

    public void Detach() => ViewModel = null;

    private void OnSurfaceChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        if (_viewModel is null || width < 80 || height < 80)
            return;

        var columns = _viewModel.Columns;
        var total = columns.Count;
        if (total == 0)
        {
            DrawText(
                context,
                _viewModel.NoDepth
                    ? "No L2 depth from this broker - pick a depth-capable one (for example, Binance)."
                    : "Waiting for the order book...",
                width / 2 - 210,
                height / 2 - 8,
                AxisTextBrush,
                12,
                420,
                TextAlignment.Center);
            return;
        }

        var plotLeft = LeftAxisWidth;
        var plotTop = TopPadding;
        var plotBottom = height - BottomAxisHeight;
        var plotRight = width - RightPadding;
        var plotWidth = plotRight - plotLeft;
        var plotHeight = plotBottom - plotTop;
        if (plotWidth < 40 || plotHeight < 40)
            return;

        var columnWidth = plotWidth / total;
        var last = columns[^1];
        var mid = last.BestBid > 0 && last.BestAsk > 0
            ? (last.BestBid + last.BestAsk) * 0.5
            : last.BestAsk > 0 ? last.BestAsk : last.BestBid;
        var step = EstimateStep(last, mid);

        double priceMin = double.MaxValue;
        double priceMax = double.MinValue;
        long maxRestingSize = 1;
        for (var columnIndex = 0; columnIndex < total; columnIndex++)
        {
            var column = columns[columnIndex];
            ScanLevels(column.Bids, ref priceMin, ref priceMax, ref maxRestingSize);
            ScanLevels(column.Asks, ref priceMin, ref priceMax, ref maxRestingSize);
        }

        if (priceMax <= priceMin)
        {
            DrawText(context, "Order book is empty...", width / 2 - 120, height / 2 - 8,
                AxisTextBrush, 12, 240, TextAlignment.Center);
            return;
        }

        if (step > 0 && mid > 0 && (priceMax - priceMin) / step > 200)
        {
            priceMin = Math.Max(priceMin, mid - 100 * step);
            priceMax = Math.Min(priceMax, mid + 100 * step);
        }

        var verticalPadding = step > 0 ? step : (priceMax - priceMin) * 0.02;
        priceMin -= verticalPadding;
        priceMax += verticalPadding;
        var priceSpan = priceMax - priceMin;
        double PriceToY(double price) => plotTop + (priceMax - price) / priceSpan * plotHeight;
        var rowHeight = Math.Max(1.5, step > 0 ? step / priceSpan * plotHeight : plotHeight / 40d);

        for (var columnIndex = 0; columnIndex < total; columnIndex++)
        {
            var column = columns[columnIndex];
            var x = plotLeft + columnIndex * columnWidth;
            DrawColumnLevels(context, column.Bids, x, columnWidth, rowHeight,
                priceMin, priceMax, priceSpan, plotTop, plotHeight, maxRestingSize);
            DrawColumnLevels(context, column.Asks, x, columnWidth, rowHeight,
                priceMin, priceMax, priceSpan, plotTop, plotHeight, maxRestingSize);
        }

        DrawMidPriceTrack(context, columns, total, plotLeft, columnWidth, priceMin, priceMax, PriceToY);
        DrawTradeBubbles(context, columns, total, plotLeft, plotWidth, priceMin, priceMax, PriceToY);
        DrawAxes(context, columns, total, plotLeft, plotRight, plotTop, plotBottom,
            plotHeight, columnWidth, priceMax, priceSpan);
    }

    private static void DrawMidPriceTrack(
        DrawingContext context,
        IReadOnlyList<DepthSnapshot> columns,
        int total,
        double plotLeft,
        double columnWidth,
        double priceMin,
        double priceMax,
        Func<double, double> priceToY)
    {
        var geometry = new PathGeometry();
        PathFigure? figure = null;
        for (var columnIndex = 0; columnIndex < total; columnIndex++)
        {
            var column = columns[columnIndex];
            if (column.BestBid <= 0 || column.BestAsk <= 0)
            {
                figure = null;
                continue;
            }

            var mid = (column.BestBid + column.BestAsk) * 0.5;
            if (mid < priceMin || mid > priceMax)
            {
                figure = null;
                continue;
            }

            var point = new Point(plotLeft + (columnIndex + 0.5) * columnWidth, priceToY(mid));
            if (figure is null)
            {
                figure = new PathFigure { StartPoint = point };
                geometry.Figures!.Add(figure);
            }
            else
            {
                figure.Segments!.Add(new LineSegment { Point = point });
            }
        }

        context.DrawGeometry(null, MidPen, geometry);
    }

    private void DrawTradeBubbles(
        DrawingContext context,
        IReadOnlyList<DepthSnapshot> columns,
        int total,
        double plotLeft,
        double plotWidth,
        double priceMin,
        double priceMax,
        Func<double, double> priceToY)
    {
        var trades = _viewModel!.RecentTrades();
        if (trades.Length == 0)
            return;

        var firstTime = columns[0].TimestampUtc.ToOADate();
        var lastTime = columns[total - 1].TimestampUtc.ToOADate();
        var timeSpan = lastTime - firstTime;
        long maxTradeSize = 1;
        foreach (var trade in trades)
            maxTradeSize = Math.Max(maxTradeSize, trade.Size);

        foreach (var trade in trades)
        {
            if (trade.Price < priceMin || trade.Price > priceMax)
                continue;
            var time = trade.Time.ToOADate();
            if (time < firstTime || time > lastTime)
                continue;

            var fraction = timeSpan > 0 ? (time - firstTime) / timeSpan : 1d;
            var x = plotLeft + Math.Clamp(fraction, 0, 1) * plotWidth;
            var y = priceToY(trade.Price);
            var radius = 2.5 + 9 * Math.Sqrt((double)trade.Size / maxTradeSize);
            var fill = trade.Side > 0
                ? BuyBubbleBrush
                : trade.Side < 0 ? SellBubbleBrush : NeutralBubbleBrush;

            if (trade.Large)
            {
                radius = Math.Max(radius, 6.5);
                context.DrawEllipse(fill, LargeBubblePen, new Point(x, y), radius, radius);
            }
            else
            {
                context.DrawEllipse(fill, BubbleOutlinePen, new Point(x, y), radius, radius);
            }
        }
    }

    private void DrawAxes(
        DrawingContext context,
        IReadOnlyList<DepthSnapshot> columns,
        int total,
        double plotLeft,
        double plotRight,
        double plotTop,
        double plotBottom,
        double plotHeight,
        double columnWidth,
        double priceMax,
        double priceSpan)
    {
        var priceTicks = Math.Clamp((int)(plotHeight / 34), 3, 12);
        for (var tick = 0; tick <= priceTicks; tick++)
        {
            var fraction = (double)tick / priceTicks;
            var price = priceMax - fraction * priceSpan;
            var y = plotTop + fraction * plotHeight;
            context.DrawLine(GridPen, new Point(plotLeft, y), new Point(plotRight, y));
            DrawText(context, price.ToString("N" + _viewModel!.PriceDecimals, CultureInfo.InvariantCulture),
                0, y - 7, PriceTextBrush, 10.5, LeftAxisWidth - 6, TextAlignment.Right);
        }

        var timeTicks = Math.Min(6, total);
        for (var tick = 0; tick < timeTicks; tick++)
        {
            var columnIndex = timeTicks == 1
                ? total - 1
                : (int)Math.Round((double)tick * (total - 1) / (timeTicks - 1));
            var x = plotLeft + (columnIndex + 0.5) * columnWidth;
            DrawText(context, columns[columnIndex].TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
                x - 34, plotBottom + 4, AxisTextBrush, 10, 68, TextAlignment.Center);
        }
    }

    private static void ScanLevels(
        IReadOnlyList<DepthLevel> levels,
        ref double priceMin,
        ref double priceMax,
        ref long maxSize)
    {
        for (var index = 0; index < levels.Count; index++)
        {
            var level = levels[index];
            if (level.Size <= 0)
                continue;
            priceMin = Math.Min(priceMin, level.Price);
            priceMax = Math.Max(priceMax, level.Price);
            maxSize = Math.Max(maxSize, level.Size);
        }
    }

    private static void DrawColumnLevels(
        DrawingContext context,
        IReadOnlyList<DepthLevel> levels,
        double x,
        double columnWidth,
        double rowHeight,
        double priceMin,
        double priceMax,
        double priceSpan,
        double plotTop,
        double plotHeight,
        long maxSize)
    {
        for (var index = 0; index < levels.Count; index++)
        {
            var level = levels[index];
            if (level.Size <= 0 || level.Price < priceMin || level.Price > priceMax)
                continue;
            var intensity = Math.Sqrt((double)level.Size / maxSize);
            var brush = HeatPalette[Math.Clamp((int)(intensity * (HeatSteps - 1)), 0, HeatSteps - 1)];
            var y = plotTop + (priceMax - level.Price) / priceSpan * plotHeight;
            context.FillRectangle(brush, new Rect(x, y - rowHeight / 2, columnWidth + 0.6, rowHeight + 0.6));
        }
    }

    private static double EstimateStep(DepthSnapshot snapshot, double mid)
    {
        var step = double.MaxValue;
        Scan(snapshot.Asks, ref step);
        Scan(snapshot.Bids, ref step);
        if (step == double.MaxValue || step <= 0)
            step = mid > 0 ? Math.Pow(10, Math.Floor(Math.Log10(mid)) - 3) : 0;
        return step;

        static void Scan(IReadOnlyList<DepthLevel> levels, ref double candidate)
        {
            for (var index = 1; index < levels.Count; index++)
            {
                var gap = Math.Abs(levels[index].Price - levels[index - 1].Price);
                if (gap > 1e-12 && gap < candidate)
                    candidate = gap;
            }
        }
    }

    private static IBrush[] BuildHeatPalette()
    {
        (double Position, byte Red, byte Green, byte Blue)[] stops =
        {
            (0.00, 0x0A, 0x0C, 0x1A),
            (0.16, 0x12, 0x26, 0x6E),
            (0.36, 0x16, 0x6E, 0xC8),
            (0.56, 0x16, 0xC0, 0xB0),
            (0.72, 0xD8, 0xCC, 0x28),
            (0.86, 0xF0, 0x8C, 0x1E),
            (1.00, 0xF6, 0x3B, 0x32),
        };
        var palette = new IBrush[HeatSteps];
        for (var index = 0; index < HeatSteps; index++)
        {
            var position = (double)index / (HeatSteps - 1);
            var stopIndex = 0;
            while (stopIndex < stops.Length - 2 && position > stops[stopIndex + 1].Position)
                stopIndex++;
            var start = stops[stopIndex];
            var end = stops[stopIndex + 1];
            var fraction = end.Position > start.Position
                ? (position - start.Position) / (end.Position - start.Position)
                : 0;
            byte Lerp(byte from, byte to) => (byte)(from + (to - from) * fraction);
            palette[index] = Brush(0xFF,
                Lerp(start.Red, end.Red), Lerp(start.Green, end.Green), Lerp(start.Blue, end.Blue));
        }
        return palette;
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        double x,
        double y,
        IBrush brush,
        double size,
        double width,
        TextAlignment alignment)
    {
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush);
        var originX = alignment switch
        {
            TextAlignment.Right => x + width - formatted.Width,
            TextAlignment.Center => x + (width - formatted.Width) / 2,
            _ => x,
        };
        context.DrawText(formatted, new Point(originX, y));
    }

    private static IBrush Brush(byte alpha, byte red, byte green, byte blue) =>
        new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));

    private static IPen Pen(byte alpha, byte red, byte green, byte blue, double thickness) =>
        new Pen(Brush(alpha, red, green, blue), thickness);
}
