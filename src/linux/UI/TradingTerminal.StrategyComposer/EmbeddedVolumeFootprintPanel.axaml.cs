using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace TradingTerminal.StrategyComposer;

/// <summary>
/// Embedded form of the destination's native Avalonia footprint surface. It intentionally exposes no
/// independent instrument, regression, or learned-forecast controls inside a strategy-owned view.
/// </summary>
public partial class EmbeddedVolumeFootprintPanel : UserControl, IEmbeddedPausable, IDisposable
{
    private RenderTargetBitmap? _freezeBitmap;

    public EmbeddedVolumeFootprintPanel() => InitializeComponent();

    public void SetPaused(bool paused)
    {
        ClearFreeze();
        if (!paused || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        _freezeBitmap = new RenderTargetBitmap(
            new PixelSize(
                Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale))),
            new Vector(96 * scale, 96 * scale));
        _freezeBitmap.Render(this);
        FreezeImage.Source = _freezeBitmap;
        FreezeImage.IsVisible = true;
    }

    public void Dispose() => ClearFreeze();

    private void ClearFreeze()
    {
        FreezeImage.IsVisible = false;
        FreezeImage.Source = null;
        _freezeBitmap?.Dispose();
        _freezeBitmap = null;
    }
}
