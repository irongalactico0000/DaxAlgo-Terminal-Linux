using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Infrastructure.StrategyAgent;

internal sealed class StrategyAgentHttpClient : IStrategyAgentClient
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly IStrategyAgentHost _host;

    public StrategyAgentHttpClient(HttpClient http, IStrategyAgentHost host)
    {
        _http = http;
        _host = host;
    }

    public Task<StrategyAgentSessionStatus> CreateSessionAsync(
        JsonElement frozenContext,
        CancellationToken cancellationToken = default) =>
        SendAsync<StrategyAgentSessionStatus>(
            HttpMethod.Post,
            "api/v1/strategy-sessions",
            new CreateSessionRequest(frozenContext),
            cancellationToken);

    public Task<StrategyAgentSessionStatus> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        SendAsync<StrategyAgentSessionStatus>(
            HttpMethod.Get,
            $"api/v1/strategy-sessions/{EscapeId(sessionId, nameof(sessionId))}",
            body: null,
            cancellationToken);

    public Task<StrategyAgentSessionStatus> SubmitMessageAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return SendAsync<StrategyAgentSessionStatus>(
            HttpMethod.Post,
            $"api/v1/strategy-sessions/{EscapeId(sessionId, nameof(sessionId))}/messages",
            new SubmitMessageRequest(message),
            cancellationToken);
    }

    public Task<StrategyAgentRunStatus> ConfirmAsync(
        string sessionId,
        StrategyAgentRunManifest manifest,
        string inputWorkspace,
        JsonElement confirmedIntent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputWorkspace);
        return SendAsync<StrategyAgentRunStatus>(
            HttpMethod.Post,
            $"api/v1/strategy-sessions/{EscapeId(sessionId, nameof(sessionId))}/confirm",
            new ConfirmRunRequest(manifest, inputWorkspace, confirmedIntent),
            cancellationToken);
    }

    public Task<StrategyAgentRunStatus> StartAsync(
        string runId,
        CancellationToken cancellationToken = default) =>
        SendAsync<StrategyAgentRunStatus>(
            HttpMethod.Post,
            $"api/v1/strategy-runs/{EscapeId(runId, nameof(runId))}/start",
            body: null,
            cancellationToken);

    public Task<StrategyAgentRunStatus> CancelAsync(
        string runId,
        CancellationToken cancellationToken = default) =>
        SendAsync<StrategyAgentRunStatus>(
            HttpMethod.Post,
            $"api/v1/strategy-runs/{EscapeId(runId, nameof(runId))}/cancel",
            body: null,
            cancellationToken);

    public Task<StrategyAgentRunStatus> GetRunAsync(
        string runId,
        CancellationToken cancellationToken = default) =>
        SendAsync<StrategyAgentRunStatus>(
            HttpMethod.Get,
            $"api/v1/strategy-runs/{EscapeId(runId, nameof(runId))}",
            body: null,
            cancellationToken);

    public Task<StrategyAgentEventPage> GetSessionEventsAsync(
        string sessionId,
        long afterSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        EnsureCursor(afterSequence);
        EnsureLimit(limit);
        return SendAsync<StrategyAgentEventPage>(
            HttpMethod.Get,
            $"api/v1/strategy-sessions/{EscapeId(sessionId, nameof(sessionId))}/events?after_seq={afterSequence}&limit={limit}",
            body: null,
            cancellationToken);
    }

    public Task<StrategyAgentEventPage> GetRunEventsAsync(
        string runId,
        long afterSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        EnsureCursor(afterSequence);
        EnsureLimit(limit);
        return SendAsync<StrategyAgentEventPage>(
            HttpMethod.Get,
            $"api/v1/strategy-runs/{EscapeId(runId, nameof(runId))}/events?after_seq={afterSequence}&limit={limit}",
            body: null,
            cancellationToken);
    }

    public Task<StrategyAgentArtifact> GetArtifactAsync(
        string runId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return SendAsync<StrategyAgentArtifact>(
            HttpMethod.Get,
            $"api/v1/strategy-runs/{EscapeId(runId, nameof(runId))}/artifacts?path={Uri.EscapeDataString(relativePath)}",
            body: null,
            cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _host.EnsureRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new StrategyAgentApiException(
                "strategy_agent_unavailable",
                "The dedicated native-strategy service is not reachable on its configured loopback port.");
        }

        using var request = new HttpRequestMessage(method, relativePath);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateApiException(response.StatusCode, payload);

            var result = JsonSerializer.Deserialize<T>(payload, JsonOptions);
            return result ?? throw new StrategyAgentApiException(
                "empty_response",
                $"The strategy-agent returned an empty response for {relativePath}.",
                response.StatusCode);
        }
        catch (StrategyAgentApiException)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StrategyAgentApiException(
                "request_timeout",
                $"The strategy-agent request timed out: {relativePath}.",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new StrategyAgentApiException(
                "strategy_agent_unreachable",
                $"The strategy-agent request could not reach the loopback service: {ex.Message}",
                innerException: ex);
        }
        catch (JsonException ex)
        {
            throw new StrategyAgentApiException(
                "invalid_response",
                $"The strategy-agent returned invalid JSON for {relativePath}: {ex.Message}",
                innerException: ex);
        }
    }

    private static StrategyAgentApiException CreateApiException(
        System.Net.HttpStatusCode statusCode,
        ReadOnlySpan<byte> payload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ApiErrorEnvelope>(payload, JsonOptions);
            if (envelope?.Detail is { Code.Length: > 0, Message.Length: > 0 } detail)
                return new StrategyAgentApiException(detail.Code, detail.Message, statusCode);
        }
        catch (JsonException)
        {
            // Fall through to a stable HTTP-stage error; malformed bodies are never treated as success.
        }

        return new StrategyAgentApiException(
            $"http_{(int)statusCode}",
            $"The strategy-agent returned HTTP {(int)statusCode} ({statusCode}).",
            statusCode);
    }

    private static string EscapeId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return Uri.EscapeDataString(value);
    }

    private static void EnsureCursor(long afterSequence)
    {
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(
                nameof(afterSequence),
                afterSequence,
                "Event cursor must be non-negative.");
    }

    private static void EnsureLimit(int limit)
    {
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "Event page limit must be between 1 and 500.");
    }

    private sealed record CreateSessionRequest(JsonElement Context);
    private sealed record SubmitMessageRequest(string Message);
    private sealed record ConfirmRunRequest(
        StrategyAgentRunManifest Manifest,
        string InputWorkspace,
        JsonElement ConfirmedIntent);
    private sealed record ApiErrorEnvelope(ApiErrorDetail Detail);
    private sealed record ApiErrorDetail(string Code, string Message);
}
