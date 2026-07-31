using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Registers the dynamic backtest-strategy registry. The macOS product intentionally ships no
/// concrete strategy implementations; authored and installed plugin options populate the registry
/// at runtime.
/// </summary>
public static class BacktestStrategyCatalog
{
    public static IServiceCollection AddBacktestStrategyCatalog(this IServiceCollection services)
    {
        foreach (var option in All)
            services.AddSingleton(option);
        services.AddSingleton<IBacktestStrategyRegistry, BacktestStrategyRegistry>();
        return services;
    }

    public static IReadOnlyList<BacktestStrategyOption> All { get; } = [];
}
