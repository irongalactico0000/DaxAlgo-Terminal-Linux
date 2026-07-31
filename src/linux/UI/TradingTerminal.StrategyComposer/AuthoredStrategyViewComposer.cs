using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.StrategyComposer;

/// <summary>
/// The Avalonia <see cref="IAuthoredStrategyViewComposer"/>: hands out a
/// <see cref="ComposedStrategyView"/> built from the descriptor's
/// <see cref="ITradingStrategy.DataRequirement"/>. The authored-strategy installer and SDK
/// bootstrap both fall back to backtest-only when no composer is registered.
/// </summary>
public sealed class AuthoredStrategyViewComposer(IServiceProvider services) : IAuthoredStrategyViewComposer
{
    /// <summary>Must run on the UI thread because it constructs Avalonia controls.</summary>
    public object ComposeView(ITradingStrategy descriptor) => new ComposedStrategyView(descriptor, services);
}

/// <summary>DI registration for the composed default strategy view.</summary>
public static class StrategyComposerServiceCollectionExtensions
{
    /// <summary>
    /// Lets the macOS shell open authored strategies that shipped no view. The host composes the
    /// live view from the strategy's declared data requirement: depth to order book, trade tape to
    /// footprint, bars to chart, all in embedded presets with no independent instrument picker.
    /// </summary>
    public static IServiceCollection AddStrategyViewComposer(this IServiceCollection services)
    {
        services.TryAddSingleton<IAuthoredStrategyViewComposer, AuthoredStrategyViewComposer>();
        return services;
    }
}
