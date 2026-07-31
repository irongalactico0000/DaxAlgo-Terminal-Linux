using System.Collections.Specialized;
using Avalonia.Controls;
using ScottPlot;

namespace TradingTerminal.Backtest.AvaloniaUi;

public partial class QuickBacktestAvaloniaWindow : Window
{
    private QuickBacktestViewModel? _viewModel;

    public QuickBacktestAvaloniaWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        Closed += (_, _) => Unbind();
    }

    private void Rebind()
    {
        Unbind();
        _viewModel = DataContext as QuickBacktestViewModel;
        if (_viewModel is null) return;
        _viewModel.EquityCurve.CollectionChanged += OnEquityChanged;
        _viewModel.EquityCurveUpdated += OnEquityUpdated;
        Redraw();
    }

    private void Unbind()
    {
        if (_viewModel is null) return;
        _viewModel.EquityCurve.CollectionChanged -= OnEquityChanged;
        _viewModel.EquityCurveUpdated -= OnEquityUpdated;
        _viewModel = null;
    }

    private void OnEquityChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();
    private void OnEquityUpdated(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        var plot = EquityPlot.Plot;
        plot.Clear();
        if (_viewModel is { EquityCurve.Count: >= 2 } vm)
            plot.Add.Signal(vm.EquityCurve.Select(point => point.Equity).ToArray());
        plot.FigureBackground.Color = Color.FromHex("#131722");
        plot.DataBackground.Color = Color.FromHex("#1E222D");
        plot.Axes.Color(Color.FromHex("#787B86"));
        EquityPlot.Refresh();
    }
}
