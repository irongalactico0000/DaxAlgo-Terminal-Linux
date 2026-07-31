using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login;

/// <summary>
/// Lazily resolves login forms only for broker clients registered on this platform. In particular,
/// NTDirect/NinjaTrader is never exposed on macOS, even if a stale native binary is present.
/// </summary>
public sealed class BrokerLoginFormFactory : IBrokerLoginFormFactory
{
    private readonly IReadOnlyDictionary<BrokerKind, IBrokerLoginForm> _byBroker;

    public BrokerLoginFormFactory(IServiceProvider services, IBrokerSelector selector)
    {
        var forms = new List<IBrokerLoginForm>();
        foreach (var registration in FormTypes)
        {
            if (!selector.IsAvailable(registration.Key)) continue;
            if (OperatingSystem.IsMacOS() && registration.Key == BrokerKind.NinjaTrader) continue;
            forms.Add((IBrokerLoginForm)services.GetRequiredService(registration.Value));
        }

        _byBroker = forms.ToDictionary(form => form.Broker);
        All = forms.OrderBy(form => (int)form.Broker).ToArray();
    }

    public IReadOnlyList<IBrokerLoginForm> All { get; }

    public IBrokerLoginForm Get(BrokerKind kind)
    {
        if (!_byBroker.TryGetValue(kind, out var form))
            throw new InvalidOperationException(
                $"No login form is available for broker {kind} on this platform.");
        return form;
    }

    private static readonly IReadOnlyDictionary<BrokerKind, Type> FormTypes =
        new Dictionary<BrokerKind, Type>
        {
            [BrokerKind.InteractiveBrokers] = typeof(Forms.IbLoginFormViewModel),
            [BrokerKind.NinjaTrader] = typeof(Forms.NinjaLoginFormViewModel),
            [BrokerKind.CTrader] = typeof(Forms.CTraderLoginFormViewModel),
            [BrokerKind.Alpaca] = typeof(Forms.AlpacaLoginFormViewModel),
            [BrokerKind.Binance] = typeof(Forms.BinanceLoginFormViewModel),
            [BrokerKind.IronBeam] = typeof(Forms.IronBeamLoginFormViewModel),
            [BrokerKind.LondonStrategicEdge] = typeof(Forms.LondonStrategicEdgeLoginFormViewModel),
            [BrokerKind.Upstox] = typeof(Forms.UpstoxLoginFormViewModel),
            [BrokerKind.Coinbase] = typeof(Forms.CoinbaseLoginFormViewModel),
            [BrokerKind.Bybit] = typeof(Forms.BybitLoginFormViewModel),
            [BrokerKind.Kraken] = typeof(Forms.KrakenLoginFormViewModel),
            [BrokerKind.Okx] = typeof(Forms.OkxLoginFormViewModel),
        };
}
