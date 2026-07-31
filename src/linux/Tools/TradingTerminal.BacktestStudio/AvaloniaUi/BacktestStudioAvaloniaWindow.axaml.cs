using Avalonia.Controls;

namespace TradingTerminal.BacktestStudio.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for Backtest Studio — net9.0-leg counterpart to the WPF
/// ScottPlot studio. Hosts the current isolated-worker run flow, progress/report metadata, and
/// portable CSV exports. The VM's replay timer remains portable via UiThread.</summary>
public partial class BacktestStudioAvaloniaWindow : Window
{
    public BacktestStudioAvaloniaWindow() => InitializeComponent();
}
