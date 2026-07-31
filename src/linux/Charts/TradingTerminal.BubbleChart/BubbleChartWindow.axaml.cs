using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace TradingTerminal.BubbleChart;

/// <summary>
/// Native Avalonia host for the copied Professional bubble-heatmap view model. The view owns only
/// renderer attachment and PNG capture; the shell owns the transient view-model lifetime exactly as
/// it does for the other chart tools.
/// </summary>
public partial class BubbleChartWindow : Window
{
    private readonly HeatmapBubbleSurface _surface;
    private readonly Control _root;

    public BubbleChartWindow()
    {
        InitializeComponent();
        _surface = this.FindControl<HeatmapBubbleSurface>("ChartSurface")!;
        _root = this.FindControl<Control>("Root")!;
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e) =>
        _surface.ViewModel = DataContext as BubbleChartViewModel;

    private async void ExportPng_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var viewModel = DataContext as BubbleChartViewModel;
        var symbol = viewModel?.SelectedInstrument?.Contract.Symbol ?? "book";
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName =
                $"bubble-heatmap-{FileToken(symbol)}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
            },
        });
        if (file is null)
            return;

        try
        {
            var scale = TopLevel.GetTopLevel(_root)?.RenderScaling ?? 1d;
            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(_root.Bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(_root.Bounds.Height * scale)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(_root);
            await using var stream = await file.OpenWriteAsync();
            bitmap.Save(stream);
            await stream.FlushAsync();
            if (viewModel is not null)
                viewModel.Status = $"Snapshot saved -> {file.Name}";
        }
        catch (Exception ex)
        {
            if (viewModel is not null)
                viewModel.Status = $"Snapshot failed: {ex.Message}";
        }
    }

    private static string FileToken(string value) =>
        value.Replace('/', '-').Replace(':', '-');

    private void OnClosed(object? sender, EventArgs e)
    {
        _surface.Detach();
        DataContextChanged -= OnDataContextChanged;
        Closed -= OnClosed;
    }
}
