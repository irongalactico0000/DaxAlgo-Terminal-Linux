using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace TradingTerminal.Infrastructure.StrategyAgent;

public static class StrategyAgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the dedicated managed process and typed loopback client for the native strategy
    /// workflow. This does not register any strategy semantics or touch the existing authoring UI.
    /// </summary>
    public static IServiceCollection AddStrategyAgent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<StrategyAgentOptions>()
            .Bind(configuration.GetSection(StrategyAgentOptions.SectionName))
            .Validate(
                static options => options.Port is >= 1 and <= 65_535,
                "StrategyAgent:Port must be between 1 and 65535.")
            .Validate(
                static options => options.StartupTimeoutSeconds >= 1,
                "StrategyAgent:StartupTimeoutSeconds must be positive.")
            .Validate(
                static options => options.RequestTimeoutSeconds >= 1,
                "StrategyAgent:RequestTimeoutSeconds must be positive.");

        services.AddSingleton<StrategyAgentHostService>();
        services.AddSingleton<IStrategyAgentHost>(
            static provider => provider.GetRequiredService<StrategyAgentHostService>());
        services.AddSingleton<IHostedService>(
            static provider => provider.GetRequiredService<StrategyAgentHostService>());

        services.AddHttpClient<IStrategyAgentClient, StrategyAgentHttpClient>(
            static (provider, client) =>
            {
                var options = provider
                    .GetRequiredService<IOptionsMonitor<StrategyAgentOptions>>()
                    .CurrentValue;
                client.BaseAddress = new Uri($"http://127.0.0.1:{options.Port}/");
                client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
            });
        return services;
    }
}
