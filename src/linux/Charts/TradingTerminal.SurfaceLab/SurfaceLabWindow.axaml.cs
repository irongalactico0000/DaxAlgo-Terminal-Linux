using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using TradingTerminal.Core.Quant.Surfaces;

namespace TradingTerminal.SurfaceLab;

/// <summary>Native Avalonia host for the copied Surface Lab view model. The view owns only
/// rendering, slice presentation, camera interaction, and PNG capture.</summary>
public partial class SurfaceLabWindow : Window
{
    private readonly SurfacePlot3D _surfacePlot;
    private readonly SurfaceSlicePlot _sliceXPlot;
    private readonly SurfaceSlicePlot _sliceYPlot;
    private SurfaceLabViewModel? _viewModel;

    public SurfaceLabWindow()
    {
        InitializeComponent();
        _surfacePlot = this.FindControl<SurfacePlot3D>("SurfacePlot")!;
        _sliceXPlot = this.FindControl<SurfaceSlicePlot>("SliceXPlot")!;
        _sliceYPlot = this.FindControl<SurfaceSlicePlot>("SliceYPlot")!;
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachViewModel();
        _viewModel = DataContext as SurfaceLabViewModel;
        _surfacePlot.ViewModel = _viewModel;
        if (_viewModel is null)
            return;

        _viewModel.SurfaceUpdated += OnSurfaceUpdated;
        _viewModel.SliceChanged += OnSliceChanged;
        if (_viewModel.HasResult)
            UpdateSliceCharts();
    }

    private void OnSurfaceUpdated(object? sender, EventArgs e) => UpdateSliceCharts();

    private void OnSliceChanged(object? sender, EventArgs e) => UpdateSliceCharts();

    private void UpdateSliceCharts()
    {
        if (_viewModel?.Result is not { } result)
        {
            _sliceXPlot.Clear();
            _sliceYPlot.Clear();
            return;
        }

        var xIndex = Math.Clamp(_viewModel.SliceXIndex, 0, result.Columns - 1);
        var yIndex = Math.Clamp(_viewModel.SliceYIndex, 0, result.Rows - 1);
        _sliceXPlot.SetData(
            result.YValues,
            SurfaceGridAnalysis.SliceAtColumn(result.Z, xIndex),
            $"{result.ZName} along {result.YName}  @  {result.XName} = {result.XLabels[xIndex]}");
        _sliceYPlot.SetData(
            result.XValues,
            SurfaceGridAnalysis.SliceAtRow(result.Z, yIndex),
            $"{result.ZName} along {result.XName}  @  {result.YName} = {result.YLabels[yIndex]}");
    }

    private async void ExportPng_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var symbol = _viewModel?.SelectedInstrument?.Contract.Symbol ?? "surface";
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"surface-{FileToken(symbol)}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
            },
        });
        if (file is null)
            return;

        try
        {
            var scale = TopLevel.GetTopLevel(_surfacePlot)?.RenderScaling ?? 1d;
            var size = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(_surfacePlot.Bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(_surfacePlot.Bounds.Height * scale)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(_surfacePlot);
            await using var stream = await file.OpenWriteAsync();
            bitmap.Save(stream);
            await stream.FlushAsync();
            if (_viewModel is not null)
                _viewModel.RunStatus = $"Snapshot saved -> {file.Name}";
        }
        catch (Exception ex)
        {
            if (_viewModel is not null)
                _viewModel.RunStatus = $"Snapshot failed: {ex.Message}";
        }
    }

    private static string FileToken(string value) =>
        value.Replace('/', '-').Replace(':', '-');

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.SurfaceUpdated -= OnSurfaceUpdated;
            _viewModel.SliceChanged -= OnSliceChanged;
        }
        _surfacePlot.ViewModel = null;
        _viewModel = null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DetachViewModel();
        DataContextChanged -= OnDataContextChanged;
        Closed -= OnClosed;
    }
}
