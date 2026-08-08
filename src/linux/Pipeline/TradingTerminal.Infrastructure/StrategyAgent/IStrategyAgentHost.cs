namespace TradingTerminal.Infrastructure.StrategyAgent;

/// <summary>Owns the dedicated local Python strategy-agent process.</summary>
public interface IStrategyAgentHost
{
    bool IsRunning { get; }

    /// <summary>Returns true when the exact loopback strategy-agent health endpoint is reachable.
    /// Launch and health failures are logged and returned as false rather than thrown.</summary>
    Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default);
}
