using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Login;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Login;

/// <summary>
/// DI registration for the login flow. The shell composition root calls <see cref="AddLogin"/>
/// once; the App project never references the concrete login window / form view-models directly.
/// The shell-handoff factory (<c>ILoginShellFactory</c>) stays in the App project since it builds
/// the main window after a successful sign-in.
/// </summary>
/// <remarks>
/// Form registration mirrors the broker-client split (<c>AddKeylessBrokers</c> /
/// <c>AddCredentialedBrokers</c> in Infrastructure): <see cref="AddLogin"/> carries only the
/// keyless-broker forms, and a shell that registers the credentialed brokers pairs it with
/// <see cref="AddCredentialedLoginForms"/>. The pairing matters because resolving
/// <c>IEnumerable&lt;IBrokerLoginForm&gt;</c> instantiates every registered form — a credentialed
/// form whose broker services are absent (e.g. cTrader's <c>ICTraderAccountDiscovery</c> in the
/// keyless-only Basic edition) would crash the login window at composition time.
/// </remarks>
public static class LoginServiceCollectionExtensions
{
    /// <summary>The login window/flow plus the KEYLESS broker forms (public crypto feeds — no API
    /// key, no account). Every edition shell calls this.</summary>
    public static IServiceCollection AddLogin(this IServiceCollection services)
    {
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<ILoginClipboard, AvaloniaLoginClipboard>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginWindow>();

        // AI Strategy Builder key store (macOS Keychain, per user) + the resolver that unlocks the keyed codegen
        // providers. Registering the resolver here replaces the Null default from AddStrategyCodegen, so
        // a stored (or {PROVIDER}_API_KEY) key makes OpenAI/DeepSeek/… available.
        services.AddSingleton<AiKeyStore>();
        services.AddSingleton<TradingTerminal.Core.Strategies.Authoring.IAiKeyStore>(sp => sp.GetRequiredService<AiKeyStore>());
        services.AddSingleton<TradingTerminal.Core.Strategies.Authoring.IAiKeyResolver, CredentialStoreAiKeyResolver>();

        // Per-broker forms are resolved lazily after the factory checks platform availability.
        services.AddSingleton<BinanceLoginFormViewModel>();

        services.AddSingleton<CoinbaseLoginFormViewModel>();

        services.AddSingleton<BybitLoginFormViewModel>();

        services.AddSingleton<KrakenLoginFormViewModel>();

        services.AddSingleton<OkxLoginFormViewModel>();

        services.AddSingleton<IBrokerLoginFormFactory, BrokerLoginFormFactory>();
        return services;
    }

    /// <summary>The CREDENTIALED broker forms (IB / NinjaTrader / cTrader / Alpaca / Ironbeam /
    /// LSE / Upstox). Call from every shell that also calls <c>AddCredentialedBrokers()</c>
    /// (Professional); the keyless-only Basic shell must not, because these
    /// forms resolve broker services only the credentialed registration provides.</summary>
    public static IServiceCollection AddCredentialedLoginForms(this IServiceCollection services)
    {
        services.AddSingleton<IbLoginFormViewModel>();

        services.AddSingleton<NinjaLoginFormViewModel>();

        services.AddSingleton<CTraderLoginFormViewModel>();

        services.AddSingleton<AlpacaLoginFormViewModel>();

        services.AddSingleton<IronBeamLoginFormViewModel>();

        services.AddSingleton<LondonStrategicEdgeLoginFormViewModel>();

        services.AddSingleton<UpstoxLoginFormViewModel>();

        return services;
    }
}
