using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login;

/// <summary>Avalonia host for the current two-pane, multi-broker login workspace.</summary>
public partial class LoginWindow : Window
{
    private LoginViewModel? _viewModel;

    public LoginWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null) _viewModel.LoginCompleted -= OnLoginCompleted;
        _viewModel = DataContext as LoginViewModel;
        if (_viewModel is not null) _viewModel.LoginCompleted += OnLoginCompleted;
    }

    private void OnLoginCompleted(object? sender, bool success)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try { Close(success); }
            catch (InvalidOperationException) { Close(); }
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null) _viewModel.LoginCompleted -= OnLoginCompleted;
        _viewModel?.Dispose();
        _viewModel = null;
        DataContextChanged -= OnDataContextChanged;
        Closed -= OnClosed;
    }
}

/// <summary>Maps dependency health to the same neutral/green/amber/red dots as the Windows login.</summary>
public sealed class ServiceStateBrushConverter : IValueConverter
{
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#787B86"));
    private static readonly IBrush Running = new SolidColorBrush(Color.Parse("#089981"));
    private static readonly IBrush Checking = new SolidColorBrush(Color.Parse("#F7A600"));
    private static readonly IBrush Stopped = new SolidColorBrush(Color.Parse("#F23645"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ServiceState.Running => Running,
        ServiceState.Checking => Checking,
        ServiceState.Stopped => Stopped,
        _ => Neutral,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Resolves the shared broker marks while preserving the initials fallback.</summary>
public sealed class BrokerLogoConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<BrokerKind, string> Assets =
        new Dictionary<BrokerKind, string>
        {
            [BrokerKind.InteractiveBrokers] = "interactive-brokers.png",
            [BrokerKind.NinjaTrader] = "ninjatrader.png",
            [BrokerKind.CTrader] = "ctrader.png",
            [BrokerKind.Alpaca] = "alpaca.png",
            [BrokerKind.Binance] = "binance.png",
            [BrokerKind.IronBeam] = "ironbeam.png",
            [BrokerKind.LondonStrategicEdge] = "london-strategic-edge.png",
            [BrokerKind.Upstox] = "upstox.png",
            [BrokerKind.Coinbase] = "coinbase.png",
            [BrokerKind.Bybit] = "bybit.png",
            [BrokerKind.Kraken] = "kraken.png",
            [BrokerKind.Okx] = "okx.png",
        };

    private static readonly Dictionary<BrokerKind, IImage?> Cache = [];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not BrokerKind broker || !Assets.TryGetValue(broker, out var asset)) return null;
        if (Cache.TryGetValue(broker, out var cached)) return cached;

        try
        {
            using var stream = AssetLoader.Open(
                new Uri($"avares://TradingTerminal.Login/Icon/Brokers/{asset}"));
            return Cache[broker] = new Bitmap(stream);
        }
        catch
        {
            return Cache[broker] = null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
