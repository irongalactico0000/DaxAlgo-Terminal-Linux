using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Charts;

/// <summary>Native-Avalonia projection of the Windows instrument picker's broker, asset and data pills.</summary>
public sealed class AvaloniaInstrumentTagsConverter : IValueConverter
{
    private static readonly IBrush White = Brush("#FFFFFF");
    private static readonly IBrush Black = Brush("#1B1B1B");
    private static readonly IBrush DataBackground = Brush("#334155");
    private static readonly IBrush DataForeground = Brush("#CFD8DC");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TradableInstrument instrument)
            return Array.Empty<AvaloniaInstrumentTag>();

        var tags = new List<AvaloniaInstrumentTag>
        {
            BrokerTag(instrument.Broker),
            CategoryTag(instrument),
            new("BAR", DataBackground, DataForeground),
            new("L1", DataBackground, DataForeground),
        };
        if (instrument.Broker == BrokerKind.CTrader)
            tags.Add(new("L2", DataBackground, DataForeground));
        if (instrument.Broker == BrokerKind.InteractiveBrokers)
            tags.Add(new("TAPE", DataBackground, DataForeground));
        return tags;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static AvaloniaInstrumentTag BrokerTag(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => new("IB", Brush("#1565C0"), White),
        BrokerKind.NinjaTrader => new("NT", Brush("#2E7D32"), White),
        BrokerKind.CTrader => new("cTrader", Brush("#6A1B9A"), White),
        BrokerKind.Alpaca => new("Alpaca", Brush("#F2A900"), Black),
        _ => new(broker.ToString(), Brush("#607D8B"), White),
    };

    private static AvaloniaInstrumentTag CategoryTag(TradableInstrument instrument)
    {
        var securityType = instrument.Contract.SecType?.ToUpperInvariant() ?? string.Empty;
        var category = instrument.Category ?? string.Empty;
        var (text, color) = securityType switch
        {
            "CASH" => ("FX", "#5C6BC0"),
            "CRYPTO" => ("CRYPTO", "#F7931A"),
            "CONTFUT" or "FUT" or "FUTURES" => ("FUT", "#8D6E63"),
            "IND" or "INDEX" => ("INDEX", "#7E57C2"),
            "OPT" => ("OPT", "#C2185B"),
            "STK" or "STOCK" when category.Contains("ETF", StringComparison.OrdinalIgnoreCase) => ("ETF", "#00897B"),
            "STK" or "STOCK" => ("STOCK", "#546E7A"),
            _ => (ShortCategory(category, securityType), "#607D8B"),
        };
        return new AvaloniaInstrumentTag(text, Brush(color), text == "CRYPTO" ? Black : White);
    }

    private static string ShortCategory(string category, string securityType)
    {
        var first = category.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first?.ToUpperInvariant() ?? (securityType.Length == 0 ? "—" : securityType);
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}

public sealed record AvaloniaInstrumentTag(string Text, IBrush Background, IBrush Foreground);
