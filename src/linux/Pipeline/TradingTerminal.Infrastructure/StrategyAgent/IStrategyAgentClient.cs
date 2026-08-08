using System.Text.Json;

namespace TradingTerminal.Infrastructure.StrategyAgent;

/// <summary>Typed client for the existing native-strategy session and run lifecycle.</summary>
public interface IStrategyAgentClient
{
    Task<StrategyAgentSessionStatus> CreateSessionAsync(
        JsonElement frozenContext,
        CancellationToken cancellationToken = default);

    Task<StrategyAgentSessionStatus> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<StrategyAgentSessionStatus> SubmitMessageAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken = default);

    Task<StrategyAgentRunStatus> ConfirmAsync(
        string sessionId,
        StrategyAgentRunManifest manifest,
        string inputWorkspace,
        JsonElement confirmedIntent,
        CancellationToken cancellationToken = default);

    Task<StrategyAgentRunStatus> StartAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<StrategyAgentRunStatus> CancelAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<StrategyAgentRunStatus> GetRunAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<StrategyAgentEventPage> GetSessionEventsAsync(
        string sessionId,
        long afterSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default);

    Task<StrategyAgentEventPage> GetRunEventsAsync(
        string runId,
        long afterSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default);

    Task<StrategyAgentArtifact> GetArtifactAsync(
        string runId,
        string relativePath,
        CancellationToken cancellationToken = default);
}
